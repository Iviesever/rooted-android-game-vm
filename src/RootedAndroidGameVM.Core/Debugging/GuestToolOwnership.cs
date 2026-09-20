using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record GuestToolResource(string Kind, string Path, string? LocalPath = null);
public sealed record GuestToolRecord(string Token, string InstanceId, string Session, string? RequestId, string? JobId,
    int OwnerPid, long OwnerStartedTicks, string Status, DateTimeOffset UpdatedAt, JsonElement? Cleanup = null, string? Error = null, GuestToolResource[]? Resources = null);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class GuestToolPolicy
{
    public static bool UsesLease(string command) => command.StartsWith("files.", StringComparison.Ordinal) && !command.StartsWith("files.tools.", StringComparison.Ordinal) ||
        command is "apps.list" or "apps.resolve" or "users.list" or "logs" or "record" or "trace" or "shell" or "root-shell";
    public static void ValidateToken(string token)
    {
        if (token.Length != 32 || token.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("guest工具token无效。");
    }
    public static string Wrap(string token, string script)
    {
        ValidateToken(token);
        var root = "/data/local/tmp/rgvm-owned-tools/" + token;
        // env creates a new initial environment, which /proc/PID/environ can identify.
        // A retained closed marker also fences launches delayed by a disconnected ADB.
        return "env RGVM_TOOL_ID=" + token + " /system/bin/sh -c " + AndroidDebugService.Q(
            "test -f " + root + "/open && test ! -e " + root + "/closed || exit 125; " + script);
    }
    public static bool OwnerAlive(GuestToolRecord record)
    {
        try { using var owner = Process.GetProcessById(record.OwnerPid); return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == record.OwnerStartedTicks; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}

public sealed partial class AndroidDebugService
{
    private sealed class GuestToolScope
    {
        public string Token { get; } = Guid.NewGuid().ToString("N");
        public GuestToolRecord? Record;
        public bool CleanupAttempted;
        public SemaphoreSlim Opening { get; } = new(1, 1);
    }
    private readonly AsyncLocal<GuestToolScope?> _guestToolScope = new();
    private readonly AsyncLocal<bool> _suppressGuestTools = new();
    private readonly ConcurrentDictionary<string, byte> _activeGuestTools = new();
    private string GuestToolRecordPath(string token)
    {
        GuestToolPolicy.ValidateToken(token);
        var path = Path.Combine(Paths.ProductRoot, "debug-runs", "guest-tools", token + ".json");
        StoragePathPolicy.RejectReparsePoints(path); return path;
    }
    private async Task SaveGuestToolRecordAsync(GuestToolRecord record)
    {
        var path = GuestToolRecordPath(record.Token); ColdCheckpoint.Restrict(Path.GetDirectoryName(path)!);
        await AtomicJsonFile.WriteAsync(path, record, CancellationToken.None); ColdCheckpoint.RestrictFile(path);
    }
    private async Task<GuestToolRecord> ReadGuestToolRecordAsync(string token, CancellationToken ct, bool requireCurrentInstance = true)
    {
        var path = GuestToolRecordPath(token);
        if (!File.Exists(path)) throw new DebugException("guest_tool_not_found", "没有该guest工具记录。", "reading_guest_tools");
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("guest工具记录超过容量限制。");
        var record = JsonSerializer.Deserialize<GuestToolRecord>(await File.ReadAllTextAsync(path, ct), AtomicJsonFile.Options) ?? throw new InvalidDataException("guest工具记录损坏。");
        if (record.Token != token || requireCurrentInstance && record.InstanceId != ReadInstanceId()) throw new DebugException("instance_mismatch", "guest工具属于其他实例。", "reading_guest_tools");
        return record;
    }
    private async Task<JsonElement> GuestToolControlAsync(string token, string op, string session, CancellationToken ct, int graceMilliseconds = 200)
    {
        var prior = _suppressGuestTools.Value; _suppressGuestTools.Value = true;
        try
        {
            RequireCatalogSession(session);
            var helper = await EnsureCatalogHelperAsync(session, ct);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(DebugJson.Write(new { op, token, graceMilliseconds })));
            var raw = await ShellAsync("CLASSPATH=" + Q(helper) + " app_process / dev.rgvm.catalog.Main tools " + Q(encoded), true, ct);
            RequireCatalogSession(session);
            using var document = JsonDocument.Parse(raw); var value = document.RootElement;
            if (!value.GetProperty("ok").GetBoolean())
                throw new DebugException("guest_tool_control_failed", value.GetProperty("error").GetProperty("message").GetString()!, "cleaning_guest_tools");
            return value.GetProperty("result").Clone();
        }
        finally { _suppressGuestTools.Value = prior; }
    }
    private async Task<string> OwnedGuestScriptAsync(string script, CancellationToken ct)
    {
        var scope = _guestToolScope.Value;
        if (scope is null || _suppressGuestTools.Value) return script;
        await scope.Opening.WaitAsync(ct);
        try
        {
            if (scope.Record is null)
            {
                using var owner = Process.GetCurrentProcess();
                var operation = DebugOperation.Current.Value;
                var priorStage = operation?.Stage;
                scope.Record = new(scope.Token, ReadInstanceId(), CatalogSession(), operation?.RequestId, operation?.JobId,
                    owner.Id, owner.StartTime.ToUniversalTime().Ticks, "opening", DateTimeOffset.UtcNow);
                await SaveGuestToolRecordAsync(scope.Record);
                if (operation is not null)
                {
                    operation.EnsureDirectory();
                    await File.WriteAllTextAsync(Path.Combine(operation.DirectoryPath, "guest-tools.json"), DebugJson.Write(new { scope.Token, cleanupPath = GuestToolRecordPath(scope.Token) }), ct);
                }
                Progress.Value?.Invoke(new { stage = "starting_guest_tools", scope.Token, session = scope.Record.Session, cleanupPath = GuestToolRecordPath(scope.Token) });
                await GuestToolControlAsync(scope.Token, "prepare", scope.Record.Session, ct);
                scope.Record = scope.Record with { Status = "active", UpdatedAt = DateTimeOffset.UtcNow };
                await SaveGuestToolRecordAsync(scope.Record);
                if (priorStage is not null) Progress.Value?.Invoke(new { stage = priorStage, scope.Token, session = scope.Record.Session, cleanupPath = GuestToolRecordPath(scope.Token) });
            }
            if (scope.Record.Status != "active") throw new DebugException("guest_tool_lease_closed", "guest工具租约已结束。", "starting_guest_tools");
            RequireCatalogSession(scope.Record.Session);
            return GuestToolPolicy.Wrap(scope.Token, script);
        }
        finally { scope.Opening.Release(); }
    }
    private async Task<string> ForegroundShellAsync(string script, bool root, CancellationToken ct)
    {
        await OwnedGuestScriptAsync("true", ct);
        var scope = _guestToolScope.Value ?? throw new InvalidOperationException("前台Shell缺少请求归属。");
        var helper = await EnsureCatalogHelperAsync(scope.Record!.Session, ct);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(DebugJson.Write(new { token = scope.Token, script })));
        return await ShellAsync("trap '' HUP; CLASSPATH=" + Q(helper) + " setsid -w app_process / dev.rgvm.catalog.Main supervise " + Q(encoded), root, ct);
    }
    private async Task<GuestToolRecord> CleanupGuestToolRecordAsync(GuestToolRecord record, bool allowEndedSession)
    {
        if (record.Status is "cleaned" or "session_ended") return record;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            if (record.InstanceId != ReadInstanceId()) throw new DebugException("instance_mismatch", "清理记录属于其他实例。");
            var session = CatalogSession();
            var endedSession = session != record.Session;
            JsonElement cleanup;
            if (endedSession)
            {
                if (!allowEndedSession) throw new DebugException("instance_mismatch", "VM会话已变化，未向新会话发送清理信号。");
                cleanup = JsonSerializer.SerializeToElement(new { clean = true, previousSessionEnded = true, killSent = false }, DebugJson.Options);
            }
            else cleanup = await GuestToolControlAsync(record.Token, "cleanup", session, deadline.Token,
                record.Resources?.Any(resource => resource.Kind == "record") == true ? 2000 : 200);
            var clean = cleanup.GetProperty("clean").GetBoolean();
            record = record with { Cleanup = cleanup, UpdatedAt = DateTimeOffset.UtcNow };
            if (clean && record.Resources is { Length: > 0 })
            {
                var resources = await CleanupDiagnosticResourcesAsync(record, session, endedSession, deadline.Token);
                var data = cleanup.Deserialize<Dictionary<string, object?>>(DebugJson.Options)!; data["resources"] = resources;
                cleanup = JsonSerializer.SerializeToElement(data, DebugJson.Options);
            }
            record = record with
            {
                Status = clean ? endedSession ? "session_ended" : "cleaned" : "pending",
                Cleanup = cleanup,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = clean ? null : "仍有进程或无法读取的进程元数据。"
            };
        }
        catch (Exception error)
        {
            var details = record.Cleanup?.Deserialize<Dictionary<string, object?>>(DebugJson.Options);
            if (details is not null)
            {
                details["processesClean"] = record.Cleanup!.Value.TryGetProperty("clean", out var clean) && clean.GetBoolean();
                details["clean"] = false; details["resourceError"] = error.Message;
            }
            record = record with
            {
                Status = "pending",
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = error.Message,
                Cleanup = details is null ? record.Cleanup : JsonSerializer.SerializeToElement(details, DebugJson.Options)
            };
        }
        await SaveGuestToolRecordAsync(record); return record;
    }
    private async Task CompleteGuestToolsAsync(bool requireClean = true)
    {
        var scope = _guestToolScope.Value;
        if (scope?.Record is null) return;
        var stage = DebugOperation.Current.Value?.Stage;
        try
        {
            if (!scope.CleanupAttempted)
            {
                scope.CleanupAttempted = true;
                scope.Record = await CleanupGuestToolRecordAsync(scope.Record, allowEndedSession: false);
            }
            if (requireClean && scope.Record.Status != "cleaned")
                throw new DebugException("guest_cleanup_required", "传输内容状态见账本；安卓端工具清理尚未核实，请明确清理后续作。", "cleaning_guest_tools", GuestToolRecordPath(scope.Token));
        }
        finally { if (stage is not null && DebugOperation.Current.Value is { } operation) operation.Stage = stage; }
    }
    public async Task<object> ExecuteAsync(DebugRequest request, CancellationToken ct)
    {
        if (!GuestToolPolicy.UsesLease(request.Command) || _guestToolScope.Value is not null) return await ExecuteCoreAsync(request, ct);
        var scope = new GuestToolScope(); _guestToolScope.Value = scope; _activeGuestTools.TryAdd(scope.Token, 0);
        try
        {
            var result = await ExecuteCoreAsync(request, ct);
            await CompleteGuestToolsAsync(); return result;
        }
        catch (Exception error)
        {
            try { await CompleteGuestToolsAsync(requireClean: false); }
            catch (Exception cleanup) { error.Data["guestCleanupError"] = cleanup.Message; }
            if (scope.Record is not null && !error.Data.Contains("guestCleanupPath")) error.Data["guestCleanupPath"] = GuestToolRecordPath(scope.Token);
            throw;
        }
        finally { _activeGuestTools.TryRemove(scope.Token, out _); _guestToolScope.Value = null; scope.Opening.Dispose(); }
    }
    public async Task<object> CleanupGuestToolsAsync(DebugRequest request, CancellationToken ct)
    {
        var token = request.Text("token"); var record = await ReadGuestToolRecordAsync(token, ct);
        if (_activeGuestTools.ContainsKey(token) || record.OwnerPid != Environment.ProcessId && GuestToolPolicy.OwnerAlive(record))
            throw new DebugException("guest_tool_active", "工具仍归属活跃任务，请先取消该任务。", "cleaning_guest_tools");
        record = await CleanupGuestToolRecordAsync(record, allowEndedSession: true);
        if (record.Status == "pending")
        {
            var pending = new DebugException("guest_cleanup_required", record.Error!, "cleaning_guest_tools", GuestToolRecordPath(token));
            pending.Data["guestCleanupPath"] = GuestToolRecordPath(token); throw pending;
        }
        return new { record, cleanupPath = GuestToolRecordPath(token) };
    }
    public async Task<object> ListGuestToolsAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(GuestToolRecordPath(new string('0', 32)))!;
        if (!Directory.Exists(directory)) return new { tools = Array.Empty<object>() };
        var instance = ReadInstanceId(); var records = new List<object>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Take(100))
        {
            var record = await ReadGuestToolRecordAsync(Path.GetFileNameWithoutExtension(file.Name), ct, requireCurrentInstance: false);
            if (record.InstanceId != instance) continue;
            records.Add(new
            {
                record,
                cleanupPath = file.FullName,
                active = _activeGuestTools.ContainsKey(record.Token),
                cleanupRequest = record.Status is "cleaned" or "session_ended" ? null : DebugRequest.Create("tools.cleanup", new { record.Token })
            });
        }
        return new { tools = records };
    }
    public async Task<GuestToolRecord[]> PendingGuestToolsAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(GuestToolRecordPath(new string('0', 32)))!;
        if (!Directory.Exists(directory)) return [];
        var instance = ReadInstanceId(); var result = new List<GuestToolRecord>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Take(100))
        {
            var record = await ReadGuestToolRecordAsync(Path.GetFileNameWithoutExtension(file.Name), ct, requireCurrentInstance: false);
            if (record.InstanceId == instance && record.Status is not ("cleaned" or "session_ended") && !_activeGuestTools.ContainsKey(record.Token) &&
                (record.OwnerPid == Environment.ProcessId || !GuestToolPolicy.OwnerAlive(record))) result.Add(record);
        }
        return result.ToArray();
    }
}
