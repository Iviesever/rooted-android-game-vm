using System.Text;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    private static TransferFingerprint Fingerprint(RemoteFileEntry entry) =>
        new(entry.Kind, entry.Bytes, entry.Version, entry.Sha256, entry.Uid, entry.Gid, entry.Mode, entry.LinkTarget, entry.ModifiedUnixMs);

    private async Task<List<RemoteFileEntry>> ScanRemoteSelectionAsync(ResolvedFileRoot root, string relative, string manifestPath, CancellationToken ct)
    {
        FileReferences.Relative(relative); RequireCatalogSession(root.Identity.Session);
        var helper = await EnsureCatalogHelperAsync(root.Identity.Session, ct);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(DebugJson.Write(new { op = "walk", root = root.Path, relativePath = relative })));
        var script = await OwnedGuestScriptAsync("CLASSPATH=" + Q(helper) + " app_process / dev.rgvm.catalog.Main fs " + Q(encoded), ct);
        Progress.Value?.Invoke(new { stage = "scanning_source", session = root.Identity.Session, manifestPath });
        await BinaryProcess.RunToFileAsync(AndroidCommandFactory.RootShell(Layout, Options, script), manifestPath, 128L * 1024 * 1024, ct);
        ColdCheckpoint.RestrictFile(manifestPath); RequireCatalogSession(root.Identity.Session);
        var entries = new List<RemoteFileEntry>(); var complete = false;
        using var reader = File.OpenText(manifestPath);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            using var document = JsonDocument.Parse(line); var row = document.RootElement;
            if (complete) throw new InvalidDataException("目录清单终态后仍有输出。");
            if (row.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
            {
                var error = row.GetProperty("error");
                throw new DebugException(error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!, "scanning_source", manifestPath);
            }
            switch (row.GetProperty("kind").GetString())
            {
                case "entry":
                    var entry = row.GetProperty("entry").Deserialize<RemoteFileEntry>(DebugJson.Options)!;
                    FileReferences.Relative(entry.RelativePath);
                    if (entry.RelativePath != relative && !entry.RelativePath.StartsWith(relative.Length == 0 ? "" : relative + "/", StringComparison.Ordinal))
                        throw new InvalidDataException("源清单越出所选目录。");
                    entries.Add(entry);
                    if (entries.Count > FileTransferPolicy.MaxEntries) throw new DebugException("transfer_limit", "传输选择超过100000项。", "scanning_source");
                    break;
                case "complete": complete = row.GetProperty("entries").GetInt32() == entries.Count; break;
                default: throw new InvalidDataException("未知清单记录。");
            }
        }
        if (!complete || entries.Count == 0) throw new DebugException("incomplete_manifest", "源清单缺少完整终态。", "scanning_source", manifestPath);
        return entries;
    }

    private static async Task<List<(string Relative, TransferFingerprint Fingerprint)>> ScanLocalSelectionAsync(string source, CancellationToken ct)
    {
        var entries = new List<(string, TransferFingerprint)>();
        var directories = new List<(string Path, TransferFingerprint Fingerprint)>();
        var pending = new Stack<(string Path, string Relative)>(); pending.Push((source, ""));
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            var fingerprint = await FileTransferPolicy.LocalFingerprintAsync(current.Path, ct) ?? throw new DebugException("source_changed", "源文件已消失。", "scanning_source", current.Path);
            entries.Add((current.Relative, fingerprint));
            if (entries.Count > FileTransferPolicy.MaxEntries) throw new DebugException("transfer_limit", "传输选择超过100000项。", "scanning_source");
            if (fingerprint.Kind == "directory")
            {
                directories.Add((current.Path, fingerprint));
                foreach (var child in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    pending.Push((child, FileTransferPolicy.JoinRemote(current.Relative, Path.GetFileName(child))));
                    if (pending.Count + entries.Count > FileTransferPolicy.MaxEntries) throw new DebugException("transfer_limit", "传输选择超过100000项。", "scanning_source");
                }
            }
        }
        foreach (var directory in directories)
            if ((await FileTransferPolicy.LocalFingerprintAsync(directory.Path, ct))?.Version != directory.Fingerprint.Version)
                throw new DebugException("source_changed", "目录在扫描时发生变化。", "scanning_source", directory.Path);
        return entries;
    }

    public async Task<object> PlanFileTransferAsync(DebugRequest request, CancellationToken ct) =>
        TransferPlanSummary(await CreateFileTransferPlanAsync(request, ct));

    private async Task<TransferPlan> CreateFileTransferPlanAsync(DebugRequest request, CancellationToken ct)
    {
        var direction = request.Text("direction");
        if (direction is not ("upload" or "download")) throw new ArgumentException("direction须为upload或download。");
        var format = request.Text("format", "directory");
        if (format is not ("directory" or "tar") || direction == "upload" && format != "directory") throw new ArgumentException("下载format为directory或tar；上传为directory。");
        var sources = request.Value<TransferSource[]>("sources") ?? [];
        if (sources.Length is < 1 or > 1024) throw new ArgumentException("sources须包含1–1024个明确来源。");
        var destination = request.Value<TransferDestination>("destination") ?? throw new ArgumentException("缺少destination。");
        var session = CatalogSession(); var instance = ReadInstanceId();
        var id = Guid.NewGuid().ToString("N"); var store = new TransferPlanStore(Paths.ProductRoot);
        var artifacts = store.DirectoryFor(id); ColdCheckpoint.Restrict(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "request.json"), DebugJson.Write(request), ct);
        FileRootIdentity? destinationIdentity = null; ResolvedFileRoot? destinationRoot = null; string destinationPath; var destinationPrefix = "";
        if (direction == "download")
        {
            if (string.IsNullOrWhiteSpace(destination.LocalDirectory) || !Path.IsPathFullyQualified(destination.LocalDirectory)) throw new ArgumentException("localDirectory须为绝对路径。");
            if (destination.EntryRef is not null || destination.RootRef is not null || destination.RelativePath.Length > 0) throw new ArgumentException("下载目标必须是本地目录。");
            destinationPath = Path.GetFullPath(destination.LocalDirectory); StoragePathPolicy.RejectReparsePoints(destinationPath);
            if (File.Exists(destinationPath)) throw new DebugException("not_directory", "下载目标不是目录。", "planning_transfer");
        }
        else
        {
            if (destination.LocalDirectory is not null) throw new ArgumentException("上传目标必须是安卓目录。");
            var targetRequest = DebugRequest.Create("files.stat", destination);
            var location = FileLocation(targetRequest); destinationIdentity = location.Root; destinationPath = location.Relative;
            destinationRoot = await ResolveFileRootAsync(location.Root, ct);
            if (request.Flag("createParents") && location.Version is null)
            {
                // Resolve from the root outwards. Nothing is created until execution.
                var paths = new List<string> { "" }; var path = "";
                foreach (var part in location.Relative.Split('/', StringSplitOptions.RemoveEmptyEntries)) { path = FileTransferPolicy.JoinRemote(path, part); paths.Add(path); }
                var ancestor = ""; var missing = false;
                var probes = paths.Select((relative, index) => new TransferPlanEntry(index, "$destination", "", relative, new("directory", 0, "probe", null), null, "new"));
                foreach (var batch in RemoteTargetBatches(probes, ""))
                {
                    var observed = await FileBridgeAsync(new { op = "batch-stat", root = destinationRoot.Path, paths = batch.Select(item => item.TargetRelativePath).ToArray(), hash = false }, session, ct);
                    foreach (var row in observed.EnumerateArray())
                    {
                        var relative = row.GetProperty("relativePath").GetString()!;
                        if (!row.GetProperty("exists").GetBoolean())
                        {
                            if (relative.Length == 0) throw new DebugException("root_unavailable", "上传根目录不存在。", "planning_transfer");
                            missing = true; continue;
                        }
                        if (missing || row.GetProperty("entry").GetProperty("kind").GetString() != "directory")
                            throw new DebugException("not_directory", "上传目标的父路径不是普通目录。", "planning_transfer");
                        ancestor = relative;
                    }
                }
                destinationPrefix = location.Relative.Length == ancestor.Length ? "" : location.Relative[(ancestor.Length == 0 ? 0 : ancestor.Length + 1)..];
                destinationPath = ancestor;
            }
            else
            {
                var target = await ObserveFileAsync(targetRequest, ct);
                if (target.Kind != "directory") throw new DebugException("not_directory", "上传目标不是目录。", "planning_transfer");
            }
        }
        var selections = new List<TransferSelection>(); var items = FileTransferPolicy.PlannedDirectories(destinationPrefix).ToList(); var issues = new List<string>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var selectionId = "s" + selections.Count;
            string sourcePath, sourceKind, prefix; string? expectedVersion; FileRootIdentity? identity;
            List<(string Relative, TransferFingerprint Fingerprint)> scanned;
            if (direction == "download")
            {
                if (source.LocalPath is not null) throw new ArgumentException("下载来源必须是安卓条目。");
                var sourceRequest = DebugRequest.Create("files.stat", source);
                var location = FileLocation(sourceRequest); identity = location.Root; sourcePath = location.Relative;
                if (selections.Any(old => old.RemoteRoot == identity && (old.SourcePath == sourcePath || old.SourceKind == "directory" &&
                    (old.SourcePath.Length == 0 || sourcePath.StartsWith(old.SourcePath + "/", StringComparison.Ordinal))))) continue;
                var root = await ResolveFileRootAsync(identity, ct);
                var remote = await ScanRemoteSelectionAsync(root, sourcePath, Path.Combine(artifacts, selectionId + ".ndjson"), ct);
                var first = remote[0];
                if (first.RelativePath != sourcePath || location.Version is not null && location.Version != first.Version)
                    throw new DebugException("stale_reference", "所选源目录或文件已变化。", "planning_transfer");
                sourceKind = first.Kind; expectedVersion = first.Version;
                prefix = sourcePath.Length == 0 ? (identity.Package ?? "shared") + "-u" + identity.UserId + "-" + identity.Kind +
                    (identity.Volume is null ? "" : "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity.Volume)))[..8].ToLowerInvariant()) : first.Name;
                scanned = remote.Select(entry => (entry.RelativePath == sourcePath ? "" : entry.RelativePath[(sourcePath.Length == 0 ? 0 : sourcePath.Length + 1)..], Fingerprint(entry))).ToList();
            }
            else
            {
                if (source.EntryRef is not null || source.RootRef is not null || source.RelativePath.Length > 0 || string.IsNullOrWhiteSpace(source.LocalPath) || !Path.IsPathFullyQualified(source.LocalPath))
                    throw new ArgumentException("上传来源localPath须为绝对路径，不能混用远端引用。");
                identity = null; sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.LocalPath));
                if (selections.Any(old => old.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase) || old.SourceKind == "directory" &&
                    sourcePath.StartsWith(old.SourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                scanned = await ScanLocalSelectionAsync(sourcePath, ct);
                sourceKind = scanned[0].Fingerprint.Kind; expectedVersion = scanned[0].Fingerprint.Version; prefix = Path.GetFileName(sourcePath);
                if (prefix.Length == 0) throw new ArgumentException("请选择具体文件或文件夹，不直接上传驱动器根。");
            }
            if (source.TargetName is not null) prefix = FileTransferPolicy.TargetName(source.TargetName);
            if (request.Flag("contentsOnly") && sourceKind == "directory")
            {
                if (source.TargetName is not null) throw new ArgumentException("contentsOnly不能与目录targetName混用。");
                prefix = "";
            }
            selections.Add(new(selectionId, sourcePath, identity, sourceKind, prefix, expectedVersion));
            foreach (var node in scanned)
            {
                if (prefix.Length == 0 && node.Relative.Length == 0 && node.Fingerprint.Kind == "directory") continue;
                var targetRelative = FileTransferPolicy.JoinRemote(destinationPrefix, FileTransferPolicy.JoinRemote(prefix, node.Relative));
                string? issue = null;
                if (node.Fingerprint.Kind is not ("file" or "directory") && !(direction == "download" && format == "tar" && node.Fingerprint.Kind == "symlink")) issue = "unsupported_entry";
                if (direction == "download" && format == "directory") issue ??= FileTransferPolicy.WindowsNameIssue(targetRelative);
                if (issue is not null) issues.Add(issue + ":" + targetRelative);
                items.Add(new(items.Count, selectionId, node.Relative, targetRelative, node.Fingerprint, null, issue is null ? "new" : "blocked", issue));
                if (items.Count > FileTransferPolicy.MaxEntries) throw new DebugException("transfer_limit", "传输选择超过100000项。", "planning_transfer");
            }
        }
        // Directory selections subsume nested selections regardless of caller order.
        var redundant = selections.Where(child => selections.Any(parent => parent != child && parent.RemoteRoot == child.RemoteRoot && parent.SourceKind == "directory" &&
            (parent.SourcePath.Length == 0 || child.SourcePath.StartsWith(parent.SourcePath + (direction == "upload" ? Path.DirectorySeparatorChar : '/'),
                direction == "upload" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))).Select(item => item.Id).ToHashSet();
        selections.RemoveAll(item => redundant.Contains(item.Id)); items.RemoveAll(item => redundant.Contains(item.SelectionId));
        items = items.Select((item, index) => item with { Index = index }).ToList();
        var comparer = direction == "download" && format == "directory" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var collisions = items.GroupBy(item => item.TargetRelativePath, comparer).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(comparer);
        for (var index = 0; index < items.Count; index++)
            if (collisions.Contains(items[index].TargetRelativePath)) items[index] = items[index] with { Conflict = "blocked", Issue = "target_collision" };
        if (direction == "download" && format == "directory")
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index]; if (item.Issue is not null) continue;
                TransferFingerprint? target;
                try { target = await FileTransferPolicy.LocalFingerprintAsync(FileTransferPolicy.LocalTarget(destinationPath, item.TargetRelativePath), ct); }
                catch (IOException) { items[index] = item with { Conflict = "blocked", Issue = "local_target_unavailable" }; continue; }
                items[index] = item with { Target = target, Conflict = FileTransferPolicy.Conflict(item.Source, target) };
            }
        }
        else if (direction == "upload")
        {
            foreach (var batch in RemoteTargetBatches(items.Where(item => item.Issue is null), destinationPath))
            {
                var paths = batch.Select(item => FileTransferPolicy.JoinRemote(destinationPath, item.TargetRelativePath)).ToArray();
                var observed = await FileBridgeAsync(new { op = "batch-stat", root = destinationRoot!.Path, paths, hash = true }, session, ct);
                var targets = observed.EnumerateArray().ToDictionary(row => row.GetProperty("relativePath").GetString()!, row => row.Clone(), StringComparer.Ordinal);
                foreach (var item in batch)
                {
                    var target = targets[FileTransferPolicy.JoinRemote(destinationPath, item.TargetRelativePath)];
                    var fingerprint = target.GetProperty("exists").GetBoolean() ? Fingerprint(target.GetProperty("entry").Deserialize<RemoteFileEntry>(DebugJson.Options)!) : null;
                    items[item.Index] = item with
                    {
                        Target = fingerprint,
                        Conflict = FileTransferPolicy.Conflict(item.Source, fingerprint),
                        Issue = CatalogText(target, "reason") == "parent_not_directory" ? "parent_type_conflict" : null
                    };
                }
            }
        }
        var roots = selections.Where(source => source.RemoteRoot is not null).Select(source => source.RemoteRoot!).Concat(destinationIdentity is null ? [] : [destinationIdentity]).Distinct().ToArray();
        var consistency = request.Text("consistency", roots.Any(root => root.Kind is "private" or "device-private") ? "stopped-app" : "live");
        if (consistency is not ("stopped-app" or "live") || direction == "upload" && (destinationIdentity?.Kind is "private" or "device-private") && consistency != "stopped-app")
            throw new ArgumentException("私有数据写入需要stopped-app；consistency仅支持stopped-app/live。");
        var applications = new List<TransferApplication>();
        if (consistency == "stopped-app") foreach (var root in roots.Where(root => root.Package is not null).DistinctBy(root => (root.Package, root.UserId)))
        {
            var current = ApplicationCatalog.Resolve(await ReadCatalogAsync(root.UserId, true, false, true, ct), new(root.InstanceId, root.UserId, root.Package!, root.InstallationRevision!));
            applications.Add(new(current.Package, current.UserId, current.InstallationRevision, current.RunningPids.Length > 0));
        }
        var total = items.Where(item => item.Source.Kind == "file").Aggregate(0L, (sum, item) => checked(sum + item.Source.Bytes));
        issues.AddRange(items.Where(item => item.Issue is not null).Select(item => item.Issue + ":" + item.TargetRelativePath));
        RequireCatalogSession(session);
        var plan = new TransferPlan(id, DateTimeOffset.UtcNow, instance, session, direction, format, consistency, selections.ToArray(), destinationIdentity,
            destinationPath, items.ToArray(), applications.ToArray(), total, 0, null, issues.Distinct().ToArray(), artifacts);
        var space = await ObserveTransferSpaceAsync(plan, destinationRoot, session, null, null, null, "overwrite", false, null, ct);
        var targetChecks = space.Checks.Where(check => check.Purposes.Any(purpose => purpose is "destination" or "destination-root" or "archive")).ToArray();
        if (space.Checks.Any(check => !check.Sufficient)) issues.Add("insufficient_space");
        plan = plan with
        {
            RequiredBytes = targetChecks.Sum(check => check.RequiredBytes),
            AvailableBytes = targetChecks.Length == 1 ? targetChecks[0].AvailableBytes : null,
            SpaceChecks = space.Checks,
            StorageBindings = space.Bindings,
            Issues = issues.Distinct().ToArray()
        };
        await store.SaveAsync(plan, ct);
        return plan;
    }
    private static IEnumerable<TransferPlanEntry[]> RemoteTargetBatches(IEnumerable<TransferPlanEntry> entries, string parent)
    {
        var batch = new List<TransferPlanEntry>(); var bytes = 0;
        foreach (var entry in entries)
        {
            var size = Encoding.UTF8.GetByteCount(DebugJson.Write(FileTransferPolicy.JoinRemote(parent, entry.TargetRelativePath)));
            if (batch.Count > 0 && (batch.Count == 100 || bytes + size > 10000)) { yield return batch.ToArray(); batch.Clear(); bytes = 0; }
            batch.Add(entry); bytes += size;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }
    private static object TransferPlanSummary(TransferPlan plan) => new
    {
        plan.PlanId,
        plan.CreatedAt,
        plan.Status,
        plan.TransferVerified,
        plan.Direction,
        plan.Format,
        plan.Consistency,
        plan.Session,
        plan.InstanceId,
        counts = plan.Entries.GroupBy(entry => entry.Conflict).ToDictionary(group => group.Key, group => group.Count()),
        totalEntries = plan.Entries.Length,
        plan.TotalBytes,
        plan.RequiredBytes,
        plan.AvailableBytes,
        plan.SpaceChecks,
        plan.ApplicationsToStop,
        plan.Issues,
        conflicts = plan.Entries.Where(entry => entry.Conflict is "different" or "type_conflict" or "blocked" || entry.Issue is not null).Take(100).ToArray(),
        preview = plan.Entries.Take(20).ToArray(),
        plan.ArtifactDirectory,
        planPath = Path.Combine(plan.ArtifactDirectory, "plan.json"),
        requiresConflictPolicy = plan.Entries.Any(entry => entry.Conflict is "different" or "type_conflict"),
        limits = new { maxEntries = FileTransferPolicy.MaxEntries },
        sourceConsistencyVerified = false
    };
    public async Task<object> ReadTransferPlanAsync(DebugRequest request, CancellationToken ct)
    {
        var plan = await new TransferPlanStore(Paths.ProductRoot).ReadAsync(request.Text("planId"), ct);
        if (plan.InstanceId != ReadInstanceId()) throw new DebugException("stale_reference", "计划属于其他实例。", "reading_transfer_plan");
        var offset = request.Number("offset", 0); var size = request.Number("pageSize", 100);
        if (offset < 0 || offset > plan.Entries.Length || size is < 1 or > 500) throw new ArgumentException("计划页范围无效。");
        var executionStore = new TransferExecutionStore(plan.ArtifactDirectory);
        var header = await executionStore.ReadHeaderAsync(ct);
        if (header is not null) header = TransferExecutionLiveness.Observe(header);
        var states = executionStore.ReadEntries();
        return new
        {
            summary = TransferPlanSummary(header is null ? plan : plan with { Status = header.Status, TransferVerified = header.Status == "succeeded" }),
            execution = header,
            executionEntries = states.Values.Where(item => item.Index >= offset && item.Index < offset + size).OrderBy(item => item.Index).ToArray(),
            entries = plan.Entries.Skip(offset).Take(size).ToArray(),
            offset,
            nextOffset = offset + size < plan.Entries.Length ? offset + size : (int?)null
        };
    }
    public async Task<object> ListTransferPlansAsync(CancellationToken ct) => new { transfers = await TransferHistoryAsync(ct) };
    public async Task<TransferPlanHistory[]> TransferHistoryAsync(CancellationToken ct)
    {
        var cards = await new TransferPlanStore(Paths.ProductRoot).ListAsync(ct);
        if (cards.Count == 0) return [];
        var instance = ReadInstanceId(); var rows = new List<TransferPlanHistory>();
        foreach (var card in cards.Where(card => card.InstanceId == instance))
        {
            var header = await new TransferExecutionStore(card.ArtifactDirectory).ReadHeaderAsync(ct);
            if (header is not null) header = TransferExecutionLiveness.Observe(header);
            var status = header?.Status ?? card.Status;
            rows.Add(new(card.PlanId, card.Direction, status, header?.UpdatedAt ?? card.CreatedAt, card.TotalEntries, card.TotalBytes,
                card.ArtifactDirectory, header is not null && TransferExecutionLiveness.CanResume(header), header?.JobId));
        }
        return rows.OrderByDescending(row => row.UpdatedAt).ToArray();
    }
}
