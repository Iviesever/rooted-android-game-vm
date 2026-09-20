using System.Text.Json;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    private async Task RegisterDiagnosticResourceAsync(GuestToolResource resource, CancellationToken ct)
    {
        await OwnedGuestScriptAsync("true", ct);
        var scope = _guestToolScope.Value ?? throw new InvalidOperationException("诊断采集需要请求归属。");
        var index = Path.Combine(Paths.ProductRoot, "debug-runs", "diagnostic-active", resource.Kind + ".json");
        StoragePathPolicy.RejectReparsePoints(index);
        if (File.Exists(index))
        {
            if (new FileInfo(index).Length > 65536) throw new InvalidDataException("诊断归属索引无效。");
            using var prior = JsonDocument.Parse(await File.ReadAllTextAsync(index, ct));
            if (prior.RootElement.GetProperty("session").GetString() == scope.Record!.Session)
            {
                var token = prior.RootElement.GetProperty("token").GetString()!;
                var owner = await ReadGuestToolRecordAsync(token, ct);
                if (owner.Status is not ("cleaned" or "session_ended"))
                {
                    var blocked = new DebugException("guest_cleanup_required", "上次同类采集尚未核实清理，请先明确恢复。", "starting_diagnostic", GuestToolRecordPath(token));
                    blocked.Data["guestCleanupPath"] = GuestToolRecordPath(token); throw blocked;
                }
            }
        }
        scope.Record = scope.Record! with { Resources = [.. scope.Record!.Resources ?? [], resource], UpdatedAt = DateTimeOffset.UtcNow };
        await SaveGuestToolRecordAsync(scope.Record);
        ColdCheckpoint.Restrict(Path.GetDirectoryName(index)!);
        await AtomicJsonFile.WriteAsync(index, new { scope.Token, session = scope.Record.Session }, ct);
    }
    private async Task<string> DiagnosticCleanupShellAsync(string script, string session, CancellationToken ct)
    {
        RequireCatalogSession(session);
        var prior = _suppressGuestTools.Value; _suppressGuestTools.Value = true;
        try { var result = await ShellAsync(script, true, ct); RequireCatalogSession(session); return result; }
        finally { _suppressGuestTools.Value = prior; }
    }
    private async Task<object[]> CleanupDiagnosticResourcesAsync(GuestToolRecord record, string session, bool endedSession, CancellationToken ct)
    {
        var results = new List<object>();
        foreach (var resource in record.Resources ?? [])
        {
            if (resource.Kind == "trace")
            {
                if (resource.Path is not ("/sys/kernel/tracing/tracing_on" or "/sys/kernel/debug/tracing/tracing_on")) throw new InvalidDataException("追踪清理路径无效。");
                if (endedSession) { results.Add(new { kind = "trace", status = "session_ended", verified = true }); continue; }
                var before = (await DiagnosticCleanupShellAsync("cat " + Q(resource.Path), session, ct)).Trim();
                if (before is not ("0" or "1")) throw new DebugException("trace_state_unknown", "无法核对内核追踪状态。", "cleaning_trace");
                if (before == "1") await DiagnosticCleanupShellAsync("atrace --async_stop >/dev/null", session, ct);
                var after = (await DiagnosticCleanupShellAsync("cat " + Q(resource.Path), session, ct)).Trim();
                if (after != "0") throw new DebugException("trace_cleanup_required", "内核追踪仍未关闭。", "cleaning_trace");
                results.Add(new { kind = "trace", status = "stopped", verified = true, resource.Path }); continue;
            }
            if (resource.Kind != "record" || resource.Path != "/data/local/tmp/rgvm-record-" + record.Token + ".mp4" || resource.LocalPath is null ||
                !StoragePathPolicy.Contains(Path.Combine(Paths.ProductRoot, "debug-runs"), Path.GetFullPath(resource.LocalPath)) || Path.GetFileName(resource.LocalPath) != "screen-no-audio.mp4")
                throw new InvalidDataException("录屏清理位置不属于该请求。");
            StoragePathPolicy.RejectReparsePoints(resource.LocalPath);
            var description = (await DiagnosticCleanupShellAsync("set -e; test ! -L " + Q(resource.Path) + "; if test -e " + Q(resource.Path) +
                "; then test -f " + Q(resource.Path) + "; stat -c %s " + Q(resource.Path) + "; sha256sum " + Q(resource.Path) + "; else echo missing; fi", session, ct)).Trim();
            if (description == "missing") { results.Add(new { kind = "record", status = "not_present", resource.LocalPath, verified = true }); continue; }
            var lines = description.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var bytes = long.Parse(lines[0].Trim()); var expected = lines[1].Split(' ')[0].Trim();
            var local = resource.LocalPath; var directory = Path.GetDirectoryName(local)!;
            if (File.Exists(local))
            {
                var existing = await ColdCheckpoint.DigestAsync(directory, local, ct);
                if (existing.Length != bytes || !existing.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new DebugException("artifact_changed", "本机录屏目标已存在且内容不同，保留guest副本。", "preserving_record", local);
            }
            else
            {
                LocalTransferSpace.Require(directory, bytes);
                var partial = local + ".partial"; StoragePathPolicy.RejectReparsePoints(partial); ColdCheckpoint.Restrict(directory);
                var prior = _suppressGuestTools.Value; _suppressGuestTools.Value = true;
                try { RequireCatalogSession(session); await AdbAsync(["pull", resource.Path, partial], ct); RequireCatalogSession(session); }
                finally { _suppressGuestTools.Value = prior; }
                ColdCheckpoint.RestrictFile(partial);
                var digest = await ColdCheckpoint.DigestAsync(directory, partial, ct);
                if (digest.Length != bytes || !digest.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new DebugException("checksum_mismatch", "录屏副本核验失败，未删除guest原件。", "preserving_record", partial);
                File.Move(partial, local);
            }
            await DiagnosticCleanupShellAsync("rm -f " + Q(resource.Path) + "; test ! -e " + Q(resource.Path) + " && test ! -L " + Q(resource.Path), session, ct);
            results.Add(new { kind = "record", status = "preserved", localPath = local, bytes, sha256 = expected, verified = true, mediaPlaybackVerified = false });
        }
        return results.ToArray();
    }
}
