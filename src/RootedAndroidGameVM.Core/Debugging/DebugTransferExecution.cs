using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    private const int TransferChunkBytes = 8 * 1024 * 1024;
    private static bool SameContent(TransferFingerprint? actual, TransferFingerprint? expected) =>
        actual is null ? expected is null : expected is not null && actual.Kind == expected.Kind && actual.Kind switch
        { "directory" => true, "symlink" => actual.LinkTarget == expected.LinkTarget, _ => actual.Bytes == expected.Bytes && actual.Sha256 == expected.Sha256 };
    private static bool SamePlannedTarget(TransferFingerprint? actual, TransferFingerprint? expected, bool mergeDirectory) =>
        SameContent(actual, expected) && (actual is null || mergeDirectory && actual.Kind == "directory" || actual.Version == expected!.Version);
    private static string MapTransferPath(string path, IEnumerable<(string Old, string New)> mappings)
    {
        var mapping = mappings.Where(item => path == item.Old || path.StartsWith(item.Old + "/", StringComparison.Ordinal)).OrderByDescending(item => item.Old.Length).FirstOrDefault();
        return mapping.Old is null ? path : mapping.New + path[mapping.Old.Length..];
    }
    private static string KeepBothPath(string path, string planId, int index)
    {
        var slash = path.LastIndexOf('/'); var name = path[(slash + 1)..];
        var suffix = ".copy-" + planId[..8] + "-" + index;
        while (Encoding.UTF8.GetByteCount(name + suffix) > 240 && name.Length > 0) name = name[..^1];
        if (name.Length > 0 && char.IsHighSurrogate(name[^1])) name = name[..^1];
        return path[..(slash + 1)] + name + suffix;
    }
    private async Task QuiesceTransferApplicationsAsync(TransferPlan plan, TransferOptions options, CancellationToken ct)
    {
        foreach (var app in plan.ApplicationsToStop)
        {
            var current = ApplicationCatalog.Resolve(await ReadCatalogAsync(app.UserId, true, false, true, ct), new(plan.InstanceId, app.UserId, app.Package, app.InstallationRevision));
            if (options.StopApplications) await ShellAsync("am force-stop --user " + app.UserId + " " + Q(app.Package), false, ct);
            var raw = await ShellAsync("ps -A -o UID,PID", false, ct);
            var live = raw.Split('\n').Skip(1).Any(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } columns && columns[0] == current.Uid.ToString());
            if (live) throw new DebugException("app_running", "一致性操作前需停止应用及其所属进程：" + app.Package, "quiescing_application");
        }
    }
    private async Task<Dictionary<int, TransferFingerprint>> VerifyTransferSourcesAsync(TransferPlan plan, Dictionary<FileRootIdentity, ResolvedFileRoot> roots, bool resume, CancellationToken ct)
    {
        var result = new Dictionary<int, TransferFingerprint>();
        foreach (var selection in plan.Selections)
        {
            List<(string Relative, TransferFingerprint Fingerprint)> observed;
            if (selection.RemoteRoot is { } identity)
            {
                var path = Path.Combine(plan.ArtifactDirectory, "verify-" + selection.Id + "-" + Guid.NewGuid().ToString("N") + ".ndjson");
                var scanned = await ScanRemoteSelectionAsync(roots[identity], selection.SourcePath, path, ct);
                observed = scanned.Select(entry => (entry.RelativePath == selection.SourcePath ? "" : entry.RelativePath[(selection.SourcePath.Length == 0 ? 0 : selection.SourcePath.Length + 1)..], Fingerprint(entry))).ToList();
            }
            else observed = await ScanLocalSelectionAsync(selection.SourcePath, ct);
            if (selection.TargetPrefix.Length == 0 && selection.SourceKind == "directory") observed.RemoveAll(entry => entry.Relative == "");
            var expected = plan.Entries.Where(entry => entry.SelectionId == selection.Id).ToArray();
            if (observed.Count != expected.Length) throw new DebugException("plan_stale", "源目录成员发生变化，请重新规划。", "verifying_sources");
            var lookup = observed.ToDictionary(entry => entry.Relative, entry => entry.Fingerprint, StringComparer.Ordinal);
            foreach (var item in expected)
            {
                if (!lookup.TryGetValue(item.RelativePath, out var actual) || !SameContent(actual, item.Source) ||
                    !resume && actual.Version != item.Source.Version || actual.Uid != item.Source.Uid || actual.Mode != item.Source.Mode)
                    throw new DebugException("plan_stale", "源文件已改变：" + item.TargetRelativePath, "verifying_sources");
                result[item.Index] = actual;
            }
        }
        return result;
    }
    private async Task<TransferFingerprint?> CurrentTransferTargetAsync(TransferPlan plan, ResolvedFileRoot? root, string relative, CancellationToken ct)
    {
        if (plan.Direction == "download") return await FileTransferPolicy.LocalFingerprintAsync(FileTransferPolicy.LocalTarget(plan.DestinationPath, relative), ct);
        var path = FileTransferPolicy.JoinRemote(plan.DestinationPath, relative);
        var data = await FileBridgeAsync(new { op = "batch-stat", root = root!.Path, paths = new[] { path }, hash = true }, root.Identity.Session, ct);
        var row = data[0];
        return row.GetProperty("exists").GetBoolean() ? Fingerprint(row.GetProperty("entry").Deserialize<RemoteFileEntry>(DebugJson.Options)!) : null;
    }
    public async Task<object> ExecuteFileTransferAsync(DebugRequest request, CancellationToken ct)
    {
        var resume = request.Command == "files.transfer.resume";
        var store = new TransferPlanStore(Paths.ProductRoot); var plan = await store.ReadAsync(request.Text("planId"), ct);
        if (plan.InstanceId != ReadInstanceId()) throw new DebugException("stale_reference", "计划属于其他实例。", "verifying_plan");
        var session = CatalogSession();
        if (!resume && plan.Session != session) throw new DebugException("stale_reference", "计划来自旧会话，请重新规划或明确续作。", "verifying_plan");
        var journal = new TransferExecutionStore(plan.ArtifactDirectory); var previous = await journal.ReadHeaderAsync(ct);
        var options = new TransferOptions(request.Text("conflictPolicy", previous?.Options.ConflictPolicy ?? "fail"),
            request.Arguments?.ContainsKey("stopApplications") == true ? request.Flag("stopApplications") : previous?.Options.StopApplications ?? false);
        if (options.ConflictPolicy is not ("fail" or "skip" or "keep-both" or "overwrite")) throw new ArgumentException("conflictPolicy须为fail/skip/keep-both/overwrite。");
        if (previous is not null && previous.Options != options) throw new DebugException("execution_options_changed", "续作不能改变原冲突/停应用策略，请重新规划。", "verifying_plan");
        if (previous is { Status: "succeeded" }) return await TransferExecutionSummaryAsync(plan, journal, ct);
        if (previous is not null && !resume) throw new DebugException("resume_required", "计划已有执行记录，请查询并明确续作。", "verifying_plan");
        if (plan.Issues.Any(issue => !issue.StartsWith("parent_type_conflict:", StringComparison.Ordinal) && issue != "insufficient_space"))
            throw new DebugException("plan_has_issues", "计划包含不能执行的名称、链接或目标冲突，请重新规划。", "verifying_plan");
        if (options.ConflictPolicy == "fail" && plan.Entries.Any(entry => entry.Conflict is "different" or "type_conflict"))
            throw new DebugException("conflict", "计划有冲突，需要明确skip/keep-both/overwrite策略。", "verifying_plan");
        using var owner = System.Diagnostics.Process.GetCurrentProcess();
        var header = new TransferExecutionHeader(plan.PlanId, options, session, DebugOperation.Current.Value?.JobId ?? request.RequestId ?? "direct", "verifying", DateTimeOffset.UtcNow,
            ArchivePath: previous?.ArchivePath, ArchiveSha256: previous?.ArchiveSha256, OwnerPid: owner.Id, OwnerStartedTicks: owner.StartTime.ToUniversalTime().Ticks);
        await journal.SaveHeaderAsync(header, ct);
        var states = journal.ReadEntries(repairTail: resume);
        var roots = new Dictionary<FileRootIdentity, ResolvedFileRoot>();
        try
        {
            foreach (var identity in plan.Selections.Where(source => source.RemoteRoot is not null).Select(source => source.RemoteRoot!).Concat(plan.DestinationRoot is null ? [] : [plan.DestinationRoot]).Distinct())
                roots[identity] = await ResolveFileRootAsync(resume ? identity with { Session = session } : identity, ct);
            await QuiesceTransferApplicationsAsync(plan, options, ct);
            var verified = await VerifyTransferSourcesAsync(plan, roots, resume, ct);
            var destinationRoot = plan.DestinationRoot is null ? null : roots[plan.DestinationRoot];
            var mappings = new List<(string Old, string New)>(); var skippedDirectories = new List<string>();
            var effective = new Dictionary<int, string>();
            foreach (var item in plan.Entries)
            {
                var target = MapTransferPath(item.TargetRelativePath, mappings);
                if (states.TryGetValue(item.Index, out var existing)) target = existing.TargetRelativePath;
                else if (options.ConflictPolicy == "keep-both" && item.Conflict is "different" or "type_conflict") target = KeepBothPath(target, plan.PlanId, item.Index);
                if (item.Source.Kind == "directory" && target != item.TargetRelativePath) mappings.Add((item.TargetRelativePath, target));
                effective[item.Index] = target;
                if (plan.Format == "tar" || existing?.Status is "committing" or "completed" or "staged" or "skipped") continue;
                if (skippedDirectories.Any(prefix => item.TargetRelativePath.StartsWith(prefix + "/", StringComparison.Ordinal))) continue;
                if (options.ConflictPolicy == "skip" && item.Conflict is "different" or "type_conflict")
                { if (item.Source.Kind == "directory") skippedDirectories.Add(item.TargetRelativePath); continue; }
                // A parent type replacement makes descendants new; they cannot exist yet.
                if (item.Issue == "parent_type_conflict") continue;
                var actual = await CurrentTransferTargetAsync(plan, destinationRoot, target, ct);
                var expected = target == item.TargetRelativePath ? item.Target : null;
                if (!SamePlannedTarget(actual, expected, item.Source.Kind == "directory" && expected?.Kind == "directory"))
                    throw new DebugException("plan_stale", "目标已改变：" + target, "verifying_targets");
            }
            if (plan.Direction == "download")
            {
                var completedBytes = plan.Entries.Where(item => states.TryGetValue(item.Index, out var state) && state.Status is "completed" or "staged" or "skipped")
                    .Where(item => item.Source.Kind == "file").Sum(item => item.Source.Bytes);
                ColdCheckpoint.RequireSpace(plan.DestinationPath, Math.Max(0, plan.RequiredBytes - completedBytes));
                StoragePathPolicy.RejectReparsePoints(plan.DestinationPath); Directory.CreateDirectory(plan.DestinationPath);
            }
            else
            {
                var available = (await FileBridgeAsync(new { op = "space", root = destinationRoot!.Path }, session, ct)).GetProperty("availableBytes").GetInt64();
                var remaining = plan.Entries.Where(item => item.Source.Kind == "file" && item.Conflict != "same")
                    .Sum(item => states.TryGetValue(item.Index, out var state) ? state.Status is "completed" or "skipped" ? 0 : Math.Max(0, item.Source.Bytes - state.Offset) : item.Source.Bytes);
                if (available < remaining + 16 * 1024 * 1024L) throw new DebugException("insufficient_space", "安卓目标剩余空间不足。", "verifying_space");
            }
            header = header with { Status = "running", UpdatedAt = DateTimeOffset.UtcNow }; await journal.SaveHeaderAsync(header, ct);
            skippedDirectories.Clear();
            foreach (var item in plan.Entries)
            {
                ct.ThrowIfCancellationRequested(); RequireCatalogSession(session);
                var selection = plan.Selections.Single(source => source.Id == item.SelectionId); var target = effective[item.Index];
                states.TryGetValue(item.Index, out var state);
                if (state?.Status is "completed" or "staged")
                {
                    if (plan.Format != "tar" || item.Source.Kind == "file")
                    {
                        var actual = plan.Format == "tar" ? await FileTransferPolicy.LocalFingerprintAsync(state.TemporaryPath!, ct) : await CurrentTransferTargetAsync(plan, destinationRoot, target, ct);
                        if (!SameContent(actual, item.Source)) throw new DebugException("target_changed", "已完成条目被修改，未重放覆盖。", "resuming_transfer");
                    }
                    continue;
                }
                if (state?.Status == "skipped") { if (item.Source.Kind == "directory") skippedDirectories.Add(item.TargetRelativePath); continue; }
                if (skippedDirectories.Any(prefix => item.TargetRelativePath.StartsWith(prefix + "/", StringComparison.Ordinal)) ||
                    options.ConflictPolicy == "skip" && item.Conflict is "different" or "type_conflict" || item.Conflict == "same")
                {
                    state = new(item.Index, "skipped", target, Error: item.Conflict == "same" ? "same_content" : "selected_skip");
                    await journal.SaveItemAsync(state, ct); states[item.Index] = state;
                    if (item.Source.Kind == "directory") skippedDirectories.Add(item.TargetRelativePath); continue;
                }
                Progress.Value?.Invoke(new
                {
                    stage = "transferring",
                    plan.PlanId,
                    index = item.Index,
                    totalEntries = plan.Entries.Length,
                    target,
                    completed = states.Values.Count(value => value.Status is "completed" or "staged" or "skipped"),
                    directory = plan.ArtifactDirectory
                });
                state ??= new(item.Index, "pending", target);
                state = plan.Direction == "download"
                    ? await DownloadTransferEntryAsync(plan, item, selection, roots[selection.RemoteRoot!], verified[item.Index], state, journal, resume, ct)
                    : await UploadTransferEntryAsync(plan, item, selection, destinationRoot!, verified[item.Index], state, journal, resume, ct);
                await journal.SaveItemAsync(state, ct); states[item.Index] = state;
            }
            if (plan.Format == "tar") header = await CommitTransferArchiveAsync(plan, header, states, journal, ct);
            if (plan.Direction == "upload") await ShellAsync("sync", true, ct);
            header = header with { Status = "succeeded", UpdatedAt = DateTimeOffset.UtcNow }; await journal.SaveHeaderAsync(header, ct);
            await store.SaveAsync(plan with { Status = "succeeded", TransferVerified = true }, ct);
            return await TransferExecutionSummaryAsync(plan, journal, ct);
        }
        catch (Exception error)
        {
            header = header with { Status = error is OperationCanceledException ? "cancelled" : "failed", UpdatedAt = DateTimeOffset.UtcNow, Error = error.Message };
            await journal.SaveHeaderAsync(header, CancellationToken.None);
            throw new DebugException(error is OperationCanceledException ? "cancelled" : error is DebugException detail ? detail.Code : "transfer_failed",
                error.Message, DebugOperation.Current.Value?.Stage ?? "transferring", journal.HeaderPath, error);
        }
    }
    private async Task<object> TransferExecutionSummaryAsync(TransferPlan plan, TransferExecutionStore store, CancellationToken ct)
    {
        var header = await store.ReadHeaderAsync(ct); if (header is not null) header = TransferExecutionLiveness.Observe(header);
        var entries = store.ReadEntries();
        return new
        {
            plan.PlanId,
            status = header?.Status ?? "planned",
            transferVerified = header?.Status == "succeeded",
            header?.JobId,
            header?.Session,
            header?.UpdatedAt,
            header?.ArchivePath,
            header?.ArchiveSha256,
            completed = entries.Values.Count(entry => entry.Status is "completed" or "staged"),
            skipped = entries.Values.Count(entry => entry.Status == "skipped"),
            totalEntries = plan.Entries.Length,
            plan.ArtifactDirectory,
            journalPath = store.EntriesPath,
            executionPath = store.HeaderPath,
            sourceConsistency = plan.Consistency,
            applicationReadVerified = false
        };
    }
}
