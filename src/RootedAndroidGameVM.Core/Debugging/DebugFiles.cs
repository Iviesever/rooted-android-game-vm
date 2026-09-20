using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    // Schema-v1 aliases are kept at the boundary; all actual I/O uses discovered roots.
    public static string RemotePath(string scope, string package, string relative)
    {
        if (scope != "shared" || !string.IsNullOrEmpty(package)) AndroidPackageName.Parse(package);
        if (relative.Contains('\\') || relative.Contains('\0') || relative.Contains('\n') || relative.Contains('\r') ||
            relative.StartsWith('/') || relative.Split('/').Any(p => p is ".." or ".")) throw new ArgumentException("安卓路径必须是范围内的相对路径。");
        var root = scope switch
        {
            "private" => "/data/data/" + package,
            "external" => "/sdcard/Android/data/" + package,
            "shared" => "/sdcard/Download",
            _ => throw new ArgumentException("scope 必须是 private、external 或 shared。")
        };
        return root + (relative.Length == 0 ? "" : "/" + relative);
    }

    private async Task<ResolvedFileRoot> LegacyFileRootAsync(string scope, string package, int user, CancellationToken ct)
    {
        var session = CatalogSession(); var instance = ReadInstanceId();
        if (user < 0) throw new ArgumentException("userId必须为非负整数。");
        CatalogApplication? app = null;
        if (scope != "shared")
        {
            var catalog = await ReadCatalogAsync(user, true, false, true, ct);
            app = catalog.Entries.SingleOrDefault(item => item.Package == package)
                ?? throw new DebugException("app_not_found", "所选用户未安装该应用。", "resolving_file");
        }
        var storage = await UserStorageAsync(user, session, ct);
        var volume = scope == "private" ? null : (storage.Volumes.SingleOrDefault(item => item.Primary)
            ?? throw new DebugException("root_unavailable", "所选用户的主共享存储不可用。", "resolving_file"));
        return await ResolveFileRootAsync(new(instance, session, user, scope, app?.Package, app?.InstallationRevision, volume?.Path), ct);
    }

    public async Task<object> FilesAsync(DebugRequest request, CancellationToken ct)
    {
        var scope = request.Text("scope", "external");
        var package = scope == "shared" ? request.Text("package") : RequirePackage(request);
        var relative = request.Text("remote"); var remote = RemotePath(scope, package, relative);
        if (request.Command == "files.push" && (relative.Length == 0 || relative.EndsWith('/')))
            throw new ArgumentException("上传目标必须包含文件名，不能是目录路径。");
        relative = string.Join('/', relative.Split('/', StringSplitOptions.RemoveEmptyEntries));
        var user = request.Number("userId", 0);
        var root = await LegacyFileRootAsync(scope, package, user, ct);
        var rootRef = FileReferences.Root(root.Identity);
        var sourceRelative = scope == "shared" ? FileTransferPolicy.JoinRemote("Download", relative) : relative;
        if (user != 0) remote = root.Path + "/" + sourceRelative;
        if (request.Command == "files.list")
        {
            var entries = new List<RemoteFileEntry>(); var cursor = request.Text("cursor");
            var paged = request.Arguments?.ContainsKey("pageSize") == true || cursor.Length > 0;
            FileBrowsePage page;
            do
            {
                page = await BrowseDirectoryAsync(DebugRequest.Create("files.browse", new
                {
                    rootRef,
                    relativePath = sourceRelative,
                    pageSize = request.Number("pageSize", 500),
                    cursor
                }), ct);
                entries.AddRange(page.Entries); cursor = page.NextCursor ?? "";
            } while (!paged && cursor.Length > 0);
            return new
            {
                scope,
                package,
                remote = request.Text("remote"),
                entries = entries.Select(LegacyFileEntry).ToArray(),
                page.Total,
                page.NextCursor,
                page.Snapshot,
                page.RootRef,
                userId = user,
                page.ObservedAt
            };
        }
        var local = request.Text("local"); var upload = request.Command is "files.push" or "files.diff" or "files.sync";
        var sync = request.Command is "files.diff" or "files.sync";
        var export = request.Command == "files.export";
        if (!upload && !export && request.Command != "files.pull") throw new ArgumentException("未知文件操作。");
        var sources = new List<TransferSource>(); TransferDestination destination; string? exportDirectory = null;
        if (upload)
        {
            if (string.IsNullOrWhiteSpace(local)) throw new ArgumentException("请选择上传来源。");
            local = Path.GetFullPath(local); StoragePathPolicy.RejectReparsePoints(local);
            if (sync && !Directory.Exists(local)) throw new DirectoryNotFoundException(local);
            if (!sync && (!File.Exists(local) || relative.Length == 0)) throw new ArgumentException("上传来源必须是文件，目标必须包含文件名。");
            var parent = sync ? sourceRelative : sourceRelative.Contains('/') ? sourceRelative[..sourceRelative.LastIndexOf('/')] : "";
            sources.Add(new(LocalPath: local, TargetName: sync ? null : sourceRelative.Split('/')[^1]));
            destination = new(RootRef: rootRef, RelativePath: parent);
        }
        else
        {
            var source = await ObserveFileAsync(DebugRequest.Create("files.stat", new { rootRef, relativePath = sourceRelative }), ct);
            if (source.Kind != (export ? "directory" : "file")) throw new DebugException("unsupported_entry", export ? "导出来源必须是普通目录。" : "下载来源必须是普通文件。", "planning_transfer");
            if (export)
            {
                if (string.IsNullOrWhiteSpace(local)) throw new ArgumentException("请选择导出保存位置。");
                exportDirectory = Path.Combine(Path.GetFullPath(local), (package.Length == 0 ? "shared" : package) + "-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
                local = Path.Combine(exportDirectory, "files");
                sources.Add(new(EntryRef: source.EntryRef)); destination = new(LocalDirectory: local);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(local)) local = Path.Combine(NewRecord("download"), source.Name);
                local = Path.GetFullPath(local);
                sources.Add(new(EntryRef: source.EntryRef, TargetName: Path.GetFileName(local)));
                destination = new(LocalDirectory: Path.GetDirectoryName(local));
            }
        }
        var plan = await CreateFileTransferPlanAsync(DebugRequest.Create("files.transfer.plan", new
        {
            direction = upload ? "upload" : "download",
            sources,
            destination,
            contentsOnly = sync || export,
            createParents = upload,
            consistency = request.Text("consistency", scope == "private" ? "stopped-app" : "live")
        }), ct);
        await File.WriteAllTextAsync(Path.Combine(plan.ArtifactDirectory, "compatibility-request.json"), DebugJson.Write(request), ct);
        Progress.Value?.Invoke(new { stage = "transfer_planned", plan.PlanId, plan.ArtifactDirectory, command = request.Command });
        object? transfer = null;
        if (request.Command != "files.diff")
            transfer = await ExecuteFileTransferAsync(DebugRequest.Create("files.transfer.start", new
            { plan.PlanId, conflictPolicy = "overwrite", stopApplications = request.Flag("stopApplications") }), ct);
        var states = new TransferExecutionStore(plan.ArtifactDirectory).ReadEntries();
        object WriteResult(TransferPlanEntry item, string target) => new
        {
            remote = target,
            backup = states.GetValueOrDefault(item.Index)?.BackupPath,
            sha256 = item.Source.Sha256,
            atomic = true,
            scope,
            permissions = states.GetValueOrDefault(item.Index)?.Permissions ?? (item.Target is { } old ? old.Uid + ":" + old.Gid + ":" + old.Mode : null),
            applicationReadVerified = false
        };
        if (sync)
            return new
            {
                deleted = false,
                results = plan.Entries.Where(item => item.Source.Kind == "file").Select(item => new
                {
                    file = FileTransferPolicy.JoinRemote(relative, item.RelativePath),
                    equal = item.Conflict == "same",
                    write = request.Command == "files.sync" && item.Conflict != "same" ? WriteResult(item, user == 0 ? RemotePath(scope, package, FileTransferPolicy.JoinRemote(relative, item.RelativePath)) :
                    root.Path + "/" + FileTransferPolicy.JoinRemote(plan.DestinationPath, item.TargetRelativePath)) : null
                }).ToArray(),
                plan.PlanId,
                plan.ArtifactDirectory,
                plan.Issues,
                transfer,
                sourceConsistency = plan.Consistency
            };
        if (export)
            return new { directory = exportDirectory, dataDirectory = local, includesPrivateData = scope == "private", plan.PlanId, plan.ArtifactDirectory, transfer, sourceConsistency = plan.Consistency };
        var file = plan.Entries.Single(item => !item.CreateDirectory);
        if (upload)
        {
            // Preserve the old flat result while also exposing the common plan and ledger.
            var fields = JsonSerializer.SerializeToElement(WriteResult(file, remote), DebugJson.Options).Deserialize<Dictionary<string, object?>>(DebugJson.Options)!;
            fields["planId"] = plan.PlanId; fields["artifactDirectory"] = plan.ArtifactDirectory; fields["transfer"] = transfer;
            return fields;
        }
        return new { local, backup = states.GetValueOrDefault(file.Index)?.BackupPath, sha256 = file.Source.Sha256, plan.PlanId, plan.ArtifactDirectory, transfer, sourceConsistency = plan.Consistency };
    }

    private static object LegacyFileEntry(RemoteFileEntry entry) => new
    {
        entry.Name,
        details = (entry.Kind switch { "directory" => "directory", "file" when entry.Bytes == 0 => "regular empty file", "file" => "regular file", "symlink" => "symbolic link", _ => entry.Kind }) +
            "|" + entry.Bytes + "|" + entry.Uid + "|" + entry.Gid + "|" + entry.Mode,
        entry.Kind,
        entry.Bytes,
        entry.EntryRef
    };
}
