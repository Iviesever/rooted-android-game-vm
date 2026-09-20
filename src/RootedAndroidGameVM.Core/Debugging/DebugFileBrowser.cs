using System.Text;
using System.Text.Json;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    private sealed record ResolvedFileRoot(FileRootIdentity Identity, string Path, string Title, bool Locked, string? UnavailableReason = null,
        int? ApplicationUid = null, string? DisplayPath = null, string? VolumeAccessPath = null);

    private async Task<JsonElement> FileBridgeAsync(object arguments, string session, CancellationToken ct)
    {
        RequireCatalogSession(session);
        var helper = await EnsureCatalogHelperAsync(session, ct);
        RequireCatalogSession(session);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(DebugJson.Write(arguments)));
        var output = await ShellAsync("CLASSPATH=" + Q(helper) + " app_process / dev.rgvm.catalog.Main fs " + Q(encoded), true, ct);
        RequireCatalogSession(session);
        using var document = JsonDocument.Parse(output);
        var reply = document.RootElement;
        if (!reply.GetProperty("ok").GetBoolean())
        {
            var error = reply.GetProperty("error");
            throw new DebugException(error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!, "observing_files");
        }
        return reply.GetProperty("result").Clone();
    }

    public async Task<object> ListAndroidUsersAsync(CancellationToken ct)
    {
        var session = CatalogSession();
        var users = (await FileBridgeAsync(new { op = "users" }, session, ct)).Deserialize<AndroidUser[]>(DebugJson.Options)!;
        return new { instanceId = ReadInstanceId(), session, users, observedAt = DateTimeOffset.UtcNow };
    }
    private async Task<AndroidUserStorage> UserStorageAsync(int user, string session, CancellationToken ct) =>
        (await FileBridgeAsync(new { op = "storage", userId = user }, session, ct)).Deserialize<AndroidUserStorage>(DebugJson.Options)!;

    public async Task<object> FileRootsAsync(DebugRequest request, CancellationToken ct)
    {
        var session = CatalogSession();
        var instance = ReadInstanceId();
        CatalogApplication? app = null;
        var user = request.Number("userId", 0);
        if (request.Text("appRef") is { Length: > 0 })
        {
            app = await ResolveApplicationAsync(request, ct);
            if (request.Arguments?.ContainsKey("userId") == true && app.UserId != user) throw new ArgumentException("userId与appRef不符。");
            user = app.UserId;
        }
        if (user < 0) throw new ArgumentException("userId必须为非负整数。");
        var storage = await UserStorageAsync(user, session, ct);
        var candidates = new List<ResolvedFileRoot>();
        if (app is not null)
        {
            foreach (var kind in new[] { "private", "device-private" })
                candidates.Add(DeriveFileRoot(new(instance, session, user, kind, app.Package, app.InstallationRevision), app, storage));
            foreach (var volume in storage.Volumes)
                foreach (var kind in new[] { "external", "obb", "media" })
                    candidates.Add(DeriveFileRoot(new(instance, session, user, kind, app.Package, app.InstallationRevision, volume.Path), app, storage));
        }
        foreach (var volume in storage.Volumes)
            candidates.Add(DeriveFileRoot(new(instance, session, user, "shared", Volume: volume.Path), null, storage));
        var paths = candidates.Where(root => root.UnavailableReason is null).Select(root => root.Path).Distinct(StringComparer.Ordinal).ToArray();
        var observations = await FileBridgeAsync(new { op = "probe", paths }, session, ct);
        var byPath = observations.EnumerateArray().ToDictionary(row => row.GetProperty("path").GetString()!, row => row.Clone(), StringComparer.Ordinal);
        var roots = candidates.Select(root =>
        {
            if (root.UnavailableReason is not null) return new FileRootDescriptor(FileReferences.Root(root.Identity), root.Identity.Kind, root.Title,
                root.Path, false, false, false, root.Locked, root.UnavailableReason, null);
            var state = byPath[root.Path];
            var exists = state.GetProperty("exists").GetBoolean();
            var accessible = !root.Locked && state.GetProperty("accessible").GetBoolean();
            var version = CatalogText(state, "version");
            var creatable = !exists && !root.Locked && root.Identity.Kind is "external" or "obb" or "media" && root.VolumeAccessPath is { } volume &&
                byPath.TryGetValue(volume, out var baseState) && baseState.GetProperty("accessible").GetBoolean() &&
                baseState.TryGetProperty("writable", out var baseWritable) && baseWritable.GetBoolean();
            return new FileRootDescriptor(FileReferences.Root(root.Identity), root.Identity.Kind, root.Title, root.DisplayPath ?? root.Path, exists, accessible,
                accessible && state.TryGetProperty("writable", out var writable) && writable.GetBoolean(), root.Locked,
                root.Locked ? "data_locked" : CatalogText(state, "reason"),
                accessible && version is not null ? FileReferences.Entry(new(root.Identity, "", version)) : null, creatable,
                root.DisplayPath is { } display && display != root.Path ? root.Path : null);
        }).ToArray();
        return new
        {
            instanceId = instance,
            session,
            userId = user,
            package = app?.Package,
            appRef = app?.AppRef,
            roots,
            observedAt = DateTimeOffset.UtcNow,
            limits = new { maxDirectoryEntries = 100000, maxPageSize = 500 }
        };
    }

    private static ResolvedFileRoot DeriveFileRoot(FileRootIdentity identity, CatalogApplication? app, AndroidUserStorage storage)
    {
        FileReferences.Validate(identity);
        if (storage.UserId != identity.UserId) throw new DebugException("stale_reference", "目录用户身份不符。", "resolving_file");
        if (identity.Kind != "shared" && (app is null || app.Package != identity.Package || app.UserId != identity.UserId || app.InstallationRevision != identity.InstallationRevision))
            throw new DebugException("stale_reference", "目录所属应用已改变。", "resolving_file");
        var volume = identity.Volume is null ? null : storage.Volumes.SingleOrDefault(volume => volume.Path == identity.Volume);
        if (identity.Volume is not null && volume is null) throw new DebugException("root_unavailable", "所选存储卷已不可用。", "resolving_file");
        var volumePath = volume is null ? null : FileReferences.VolumeAccessPath(volume, identity.UserId);
        var path = identity.Kind switch
        {
            "private" => app!.CredentialProtectedDirectory ?? app.PrivateDirectory,
            "device-private" => app!.DeviceProtectedDirectory,
            "external" => volumePath!.TrimEnd('/') + "/Android/data/" + app!.Package,
            "obb" => volumePath!.TrimEnd('/') + "/Android/obb/" + app!.Package,
            "media" => volumePath!.TrimEnd('/') + "/Android/media/" + app!.Package,
            "shared" => volumePath,
            _ => null
        };
        var title = identity.Kind switch { "private" => "私有数据 · Root", "device-private" => "设备保护数据 · Root", "external" => "应用外部文件", "obb" => "OBB资源", "media" => "应用媒体", _ => "共享存储" };
        if (volume is { Primary: false }) title += " · " + volume.Path.Split('/')[^1];
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) return new(identity, "", title, !storage.Unlocked, "metadata_unavailable");
        var displayPath = volume is null ? path : volume.Path.TrimEnd('/') + path[volumePath!.TrimEnd('/').Length..];
        return new(identity, path, title, !storage.Unlocked && identity.Kind != "device-private", ApplicationUid: app?.Uid,
            DisplayPath: displayPath, VolumeAccessPath: volumePath);
    }
    private async Task<ResolvedFileRoot> ResolveFileRootAsync(FileRootIdentity identity, CancellationToken ct)
    {
        FileReferences.Validate(identity);
        if (identity.InstanceId != ReadInstanceId() || identity.Session != CatalogSession())
            throw new DebugException("stale_reference", "文件引用来自其他实例或旧运行会话，请重新获取目录。", "resolving_file");
        CatalogApplication? app = null;
        if (identity.Kind != "shared")
        {
            var reference = new ApplicationReference(identity.InstanceId, identity.UserId, identity.Package!, identity.InstallationRevision!);
            var snapshot = await ReadCatalogAsync(identity.UserId, true, false, true, ct);
            app = ApplicationCatalog.Resolve(snapshot, reference);
        }
        var storage = await UserStorageAsync(identity.UserId, identity.Session, ct);
        var root = DeriveFileRoot(identity, app, storage);
        if (root.UnavailableReason is not null) throw new DebugException("root_unavailable", "系统未提供该数据根。", "resolving_file");
        if (root.Locked) throw new DebugException("data_locked", "该Android用户的数据尚未解锁。", "resolving_file");
        return root;
    }
    private static (FileRootIdentity Root, string Relative, string? Version) FileLocation(DebugRequest request)
    {
        if (request.Text("entryRef") is { Length: > 0 } entryRef)
        {
            if (request.Text("rootRef").Length > 0 || request.Text("relativePath").Length > 0) throw new ArgumentException("entryRef和rootRef/relativePath不能混用。");
            var entry = FileReferences.ReadEntry(entryRef);
            return (entry.Root, entry.RelativePath, entry.Version);
        }
        var root = FileReferences.ReadRoot(request.Text("rootRef"));
        var relative = request.Text("relativePath"); FileReferences.Relative(relative);
        return (root, relative, null);
    }
    private static RemoteFileEntry ReferencedEntry(JsonElement value, FileRootIdentity root) =>
        ReferencedEntry(value.Deserialize<RemoteFileEntry>(DebugJson.Options)!, root);
    private static RemoteFileEntry ReferencedEntry(RemoteFileEntry value, FileRootIdentity root) =>
        value with { EntryRef = FileReferences.Entry(new(root, value.RelativePath, value.Version)) };

    public async Task<FileBrowsePage> BrowseDirectoryAsync(DebugRequest request, CancellationToken ct)
    {
        var location = FileLocation(request);
        var size = request.Number("pageSize", 200);
        if (size is < 1 or > 500) throw new ArgumentException("pageSize必须为1–500。");
        var cursor = request.Text("cursor") is { Length: > 0 } encoded ? FileReferences.ReadCursor(encoded, location.Root, location.Relative) : null;
        var root = await ResolveFileRootAsync(location.Root, ct);
        var result = await FileBridgeAsync(new
        {
            op = "list",
            root = root.Path,
            relativePath = location.Relative,
            offset = cursor?.Offset ?? 0,
            limit = size,
            snapshot = cursor?.Snapshot
        }, root.Identity.Session, ct);
        var directory = ReferencedEntry(result.GetProperty("directory"), root.Identity);
        if (location.Version is not null && location.Version != directory.Version) throw new DebugException("stale_reference", "目录已改变，请刷新目录引用。", "browsing_files");
        var entries = result.GetProperty("entries").EnumerateArray().Select(row => ReferencedEntry(row, root.Identity)).ToArray();
        var snapshot = result.GetProperty("snapshot").GetString()!;
        var nextOffset = result.GetProperty("nextOffset");
        var next = nextOffset.ValueKind == JsonValueKind.Number ? FileReferences.Cursor(new(root.Identity, location.Relative, snapshot, nextOffset.GetInt32())) : null;
        return new(FileReferences.Root(root.Identity), location.Relative, directory, entries, result.GetProperty("total").GetInt32(), next, snapshot, DateTimeOffset.UtcNow);
    }
    public async Task<RemoteFileEntry> ObserveFileAsync(DebugRequest request, CancellationToken ct)
    {
        var location = FileLocation(request);
        var root = await ResolveFileRootAsync(location.Root, ct);
        var result = ReferencedEntry(await FileBridgeAsync(new { op = "stat", root = root.Path, relativePath = location.Relative, hash = request.Flag("hash") }, root.Identity.Session, ct), root.Identity);
        if (location.Version is not null && result.Version != location.Version) throw new DebugException("stale_reference", "文件已改变，请刷新条目引用。", "observing_files");
        return result;
    }
}
