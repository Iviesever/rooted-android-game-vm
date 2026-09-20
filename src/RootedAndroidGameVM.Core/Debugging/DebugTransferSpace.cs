using System.Text.Json;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    private static int MissingDestinationDirectories(string path)
    {
        StoragePathPolicy.RejectReparsePoints(path); var count = 0;
        for (var current = Path.GetFullPath(path); !Directory.Exists(current); current = Path.GetDirectoryName(current) ?? throw new IOException("没有本地目标卷。"))
        {
            if (File.Exists(current)) throw new DebugException("not_directory", "本地目标父路径是文件。", "verifying_space");
            count++;
        }
        return count;
    }
    private async Task<(TransferSpaceCheck[] Checks, TransferStorageBinding[] Bindings)> ObserveTransferSpaceAsync(TransferPlan plan,
        ResolvedFileRoot? destinationRoot, string session, IReadOnlyDictionary<int, TransferItemState>? states, IReadOnlyDictionary<int, TransferSpaceProgress>? progress,
        IReadOnlyDictionary<int, string>? effective, string policy, bool executing, TransferExecutionHeader? header, CancellationToken ct)
    {
        var archiveComplete = false; long reclaimable = 0;
        if (plan.Format == "tar" && executing)
        {
            var archive = Path.Combine(plan.DestinationPath, "app-data-" + plan.PlanId + ".tar");
            StoragePathPolicy.RejectReparsePoints(archive);
            if (File.Exists(archive))
            {
                if (header?.ArchiveSha256 is null || await PrefixHashAsync(archive, new FileInfo(archive).Length, ct) != header.ArchiveSha256)
                    throw new DebugException("target_changed", "归档目标已存在且不能核对归属。", "verifying_space");
                archiveComplete = true;
            }
            else reclaimable = LocalTransferSpace.ReclaimableBytes(archive + ".partial");
        }
        var demands = TransferSpacePolicy.Demands(plan, destinationRoot?.Path ?? "", states, progress, effective, policy,
            archiveComplete, reclaimable, plan.Direction == "download" ? MissingDestinationDirectories(plan.DestinationPath) : 0).ToList();
        var observations = new Dictionary<(string Endpoint, string Path), TransferSpaceObservation>();
        var bindings = new List<TransferStorageBinding>();
        foreach (var group in demands.Where(demand => demand.Endpoint == "guest").GroupBy(demand => demand.Purpose == "wire" ? "/data/local/tmp" : destinationRoot!.Path))
        {
            var root = group.Key;
            var paths = group.Select(demand => demand.Path).Distinct(StringComparer.Ordinal).Select((path, index) =>
            {
                if (path != root && !path.StartsWith(root + "/", StringComparison.Ordinal)) throw new DebugException("path_escape", "空间查询越出目标根。", "verifying_space");
                var relative = path == root ? "" : path[(root.Length + 1)..];
                return new TransferPlanEntry(index, "$space", "", relative, new("directory", 0, "space", null), null, "new");
            });
            foreach (var batch in RemoteTargetBatches(paths, ""))
            {
                var rows = await FileBridgeAsync(new { op = "spaces", root, paths = batch.Select(entry => entry.TargetRelativePath).ToArray() }, session, ct);
                foreach (var row in rows.EnumerateArray())
                {
                    var path = FileTransferPolicy.JoinRemote(root, row.GetProperty("relativePath").GetString()!);
                    var resolved = FileTransferPolicy.JoinRemote(root, row.GetProperty("resolvedRelativePath").GetString()!);
                    var observation = new TransferSpaceObservation("guest", path, resolved, row.GetProperty("volumeId").GetString()!,
                        row.GetProperty("availableBytes").GetInt64(), row.GetProperty("allocationUnitBytes").GetInt64());
                    observations[("guest", path)] = observation; bindings.Add(new("guest", resolved, observation.VolumeId));
                }
            }
        }
        if (plan.Direction == "upload")
        {
            var guestChecks = TransferSpacePolicy.Combine(demands.Where(demand => demand.Endpoint == "guest"), observations);
            var growth = guestChecks.Sum(check => check.GrowthBytes);
            if (growth > 0) demands.Add(new("host", Paths.AvdHome, "vm-backing", Bytes: growth));
        }
        foreach (var path in demands.Where(demand => demand.Endpoint == "host").Select(demand => demand.Path).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var observation = LocalTransferSpace.Observe(path); observations[("host", path)] = observation;
            bindings.Add(new("host", observation.ResolvedPath, observation.VolumeId));
        }
        static string BindingKey(string endpoint, string path) => endpoint + ":" + (endpoint == "host" ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant() : path.TrimEnd('/'));
        var oldBindings = (plan.StorageBindings ?? []).GroupBy(binding => BindingKey(binding.Endpoint, binding.Path))
            .ToDictionary(group => group.Key, group => group.First().VolumeId, StringComparer.Ordinal);
        foreach (var observation in observations.Values)
        {
            if (observation.AvailableBytes < 0 || observation.AllocationUnitBytes < 0) throw new InvalidDataException("容量观察无效。");
            if (!executing || plan.StorageBindings is null || observation.Endpoint == "guest" && plan.Session != session) continue;
            for (string? path = observation.Path; !string.IsNullOrEmpty(path); path = observation.Endpoint == "host" ? Path.GetDirectoryName(path) : path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : null)
                if (oldBindings.TryGetValue(BindingKey(observation.Endpoint, path), out var prior))
                {
                    if (prior != observation.VolumeId) throw new DebugException("plan_stale", "分配路径所在卷已变化，请重新规划：" + observation.Path, "verifying_space");
                    break;
                }
        }
        var checks = TransferSpacePolicy.Combine(demands, observations);
        return (checks, bindings.Distinct().ToArray());
    }
    private async Task<Dictionary<int, TransferSpaceProgress>> VerifyTransferSpaceProgressAsync(TransferPlan plan, ResolvedFileRoot? destinationRoot,
        Dictionary<FileRootIdentity, ResolvedFileRoot> roots, Dictionary<int, TransferFingerprint> verified, IReadOnlyDictionary<int, TransferItemState> states,
        IReadOnlyDictionary<int, string> effective, CancellationToken ct)
    {
        var result = new Dictionary<int, TransferSpaceProgress>();
        foreach (var item in plan.Entries)
        {
            if (!states.TryGetValue(item.Index, out var state)) continue;
            if (state.Status == "skipped" && state.Error != "same_content") continue;
            var target = effective[item.Index];
            if (item.Source.Kind == "directory" && state.Status == "committing")
            {
                var actualDirectory = await CurrentTransferTargetAsync(plan, destinationRoot, target, ct);
                if (actualDirectory?.Kind == "directory") result[item.Index] = new(0, true, true);
                else if (!SamePlannedTarget(actualDirectory, target == item.TargetRelativePath ? item.Target : null, false))
                    throw new DebugException("target_changed", "未完成目录的目标已改变。", "verifying_space");
                continue;
            }
            if (state.Status is "completed" or "staged" or "skipped" || item.Source.Kind == "directory")
            {
                TransferFingerprint? actual = null;
                if (plan.Format != "tar") actual = await CurrentTransferTargetAsync(plan, destinationRoot, target, ct);
                else if (item.Source.Kind == "file") actual = await FileTransferPolicy.LocalFingerprintAsync(Path.Combine(plan.ArtifactDirectory, "archive-content", item.Index + ".data"), ct);
                if (plan.Format != "tar" || item.Source.Kind == "file")
                {
                    if (!SameContent(actual, item.Source)) throw new DebugException("target_changed", "已记录条目的内容已改变：" + target, "verifying_space");
                }
                result[item.Index] = new(item.Source.Bytes, true, true); continue;
            }
            if (item.Source.Kind != "file" || state.Status is not ("staging" or "committing")) continue;
            var selection = plan.Selections.Single(source => source.Id == item.SelectionId);
            long length; bool exists; string? hash;
            if (plan.Direction == "upload")
            {
                var data = await FileBridgeAsync(new
                {
                    op = "transfer-state",
                    root = destinationRoot!.Path,
                    relativePath = FileTransferPolicy.JoinRemote(plan.DestinationPath, target),
                    planId = plan.PlanId,
                    index = item.Index
                }, destinationRoot.Identity.Session, ct);
                if (state.Status == "committing" && data.TryGetProperty("target", out var current) && SameContent(Fingerprint(current.Deserialize<RemoteFileEntry>(DebugJson.Options)!), item.Source))
                { result[item.Index] = new(item.Source.Bytes, true, true); continue; }
                length = data.GetProperty("bytes").GetInt64(); hash = CatalogText(data, "sha256"); exists = hash is not null;
                if (length > item.Source.Bytes) throw new DebugException("staging_changed", "暂存超过源文件长度。", "verifying_space");
                var source = item.RelativePath.Length == 0 ? selection.SourcePath : Path.Combine(selection.SourcePath, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (exists && await PrefixHashAsync(source, length, ct) != hash) throw new DebugException("staging_mismatch", "暂存前缀不符，未抵扣容量。", "verifying_space");
            }
            else
            {
                var destination = FileTransferPolicy.LocalTarget(plan.DestinationPath, target);
                if (state.Status == "committing" && plan.Format != "tar" && SameContent(await FileTransferPolicy.LocalFingerprintAsync(destination, ct), item.Source))
                { result[item.Index] = new(item.Source.Bytes, true, true); continue; }
                var stage = plan.Format == "tar" ? Path.Combine(plan.ArtifactDirectory, "archive-content", item.Index + ".data") : TransferScratch(destination, plan.PlanId, item.Index, "stage");
                StoragePathPolicy.RejectReparsePoints(stage); exists = File.Exists(stage); length = exists ? new FileInfo(stage).Length : 0;
                if (length > item.Source.Bytes) throw new DebugException("staging_changed", "暂存超过源文件长度。", "verifying_space");
                if (exists)
                {
                    var root = roots[selection.RemoteRoot!];
                    var prefix = await FileBridgeAsync(new
                    {
                        op = "transfer-hash-prefix",
                        root = root.Path,
                        relativePath = FileTransferPolicy.JoinRemote(selection.SourcePath, item.RelativePath),
                        version = verified[item.Index].Version,
                        length
                    }, root.Identity.Session, ct);
                    hash = prefix.GetProperty("sha256").GetString();
                    if (await PrefixHashAsync(stage, length, ct) != hash) throw new DebugException("staging_mismatch", "暂存前缀不符，未抵扣容量。", "verifying_space");
                }
            }
            result[item.Index] = new(length, exists);
        }
        return result;
    }
}
