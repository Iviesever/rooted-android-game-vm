using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record GuestToolRecord(string Token, string InstanceId, string Session, string? RequestId, string? JobId,
    int OwnerPid, long OwnerStartedTicks, string Status, DateTimeOffset UpdatedAt, JsonElement? Cleanup = null, string? Error = null);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class GuestToolPolicy
{
    public static bool UsesLease(string command) => command.StartsWith("files.", StringComparison.Ordinal) && !command.StartsWith("files.tools.", StringComparison.Ordinal) ||
        command is "apps.list" or "apps.resolve" or "users.list";
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
    private async Task<JsonElement> GuestToolControlAsync(string token, string op, string session, CancellationToken ct)
    {
        var prior = _suppressGuestTools.Value; _suppressGuestTools.Value = true;
        try
        {
            RequireCatalogSession(session);
            var helper = await EnsureCatalogHelperAsync(session, ct);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(DebugJson.Write(new { op, token })));
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
    private async Task<GuestToolRecord> CleanupGuestToolRecordAsync(GuestToolRecord record, bool allowEndedSession)
    {
        if (record.Status is "cleaned" or "session_ended") return record;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            if (record.InstanceId != ReadInstanceId()) throw new DebugException("instance_mismatch", "清理记录属于其他实例。");
            var session = CatalogSession();
            if (session != record.Session)
            {
                if (!allowEndedSession) throw new DebugException("instance_mismatch", "VM会话已变化，未向新会话发送清理信号。");
                record = record with { Status = "session_ended", UpdatedAt = DateTimeOffset.UtcNow, Error = "原VM会话已结束；未向新会话发送kill，文件暂存仍保留。" };
            }
            else
            {
                var cleanup = await GuestToolControlAsync(record.Token, "cleanup", session, deadline.Token);
                record = record with
                {
                    Status = cleanup.GetProperty("clean").GetBoolean() ? "cleaned" : "pending",
                    Cleanup = cleanup,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = cleanup.GetProperty("clean").GetBoolean() ? null : "仍有进程或无法读取的进程元数据。"
                };
            }
        }
        catch (Exception error) { record = record with { Status = "pending", UpdatedAt = DateTimeOffset.UtcNow, Error = error.Message }; }
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
            if (scope.Record is not null) error.Data["guestCleanupPath"] = GuestToolRecordPath(scope.Token);
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
        if (record.Status == "pending") throw new DebugException("guest_cleanup_required", record.Error!, "cleaning_guest_tools", GuestToolRecordPath(token));
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
                cleanupRequest = record.Status is "cleaned" or "session_ended" ? null : DebugRequest.Create("files.tools.cleanup", new { record.Token })
            });
        }
        return new { tools = records };
    }
}
