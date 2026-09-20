using System.Security.Cryptography;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly SemaphoreSlim _helperGate = new(1, 1);
    private readonly Dictionary<(int User, bool System, bool Icons), ApplicationCatalogSnapshot> _catalogCache = [];

    private string CatalogSession()
    {
        var host = Instance.Require(force: true);
        return $"{host.ProcessId}:{host.StartedAtUtcTicks}";
    }
    private void RequireCatalogSession(string expected)
    {
        if (CatalogSession() != expected) throw new DebugException("instance_mismatch", "读取应用期间实例已改变，请重新发现应用。", "listing_applications");
    }
    private string ReadInstanceId()
    {
        if (!StorageOwnership.IsOwned(Paths.ProductRoot)) throw new DebugException("instance_mismatch", "应用清单需要产品拥有的资源目录。");
        var path = Path.Combine(Paths.ProductRoot, ".rgvm-instance-id");
        StoragePathPolicy.RejectReparsePoints(path);
        if (!File.Exists(path))
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")));
                    stream.Flush(flushToDisk: true);
                }
                ColdCheckpoint.RestrictFile(temporary);
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (new FileInfo(path).Length != 32) throw new DebugException("instance_identity_invalid", "实例身份文件长度无效。", "listing_applications", path);
        ColdCheckpoint.RestrictFile(path);
        var value = File.ReadAllText(path).Trim();
        if (!Guid.TryParseExact(value, "N", out _)) throw new DebugException("instance_identity_invalid", "实例身份文件无效，未生成替代身份。", "listing_applications", path);
        return value;
    }

    public async Task<object> ListApplicationsAsync(DebugRequest request, CancellationToken ct)
    {
        var user = request.Number("userId", 0);
        if (user < 0) throw new ArgumentException("userId必须为非负整数。");
        var snapshot = await ReadCatalogAsync(user, request.Flag("includeSystem"), request.Flag("includeIcons"), request.Flag("refresh"), ct);
        return ApplicationCatalog.Page(snapshot, request.Text("query"), request.Number("pageSize", 100), request.Text("cursor"));
    }

    public async Task<CatalogApplication> ResolveApplicationAsync(DebugRequest request, CancellationToken ct)
    {
        var identity = ApplicationCatalog.ParseReference(request.Text("appRef"));
        if (identity.InstanceId != ReadInstanceId()) throw new DebugException("stale_reference", "应用引用属于其他实例。", "resolving_application");
        var snapshot = await ReadCatalogAsync(identity.UserId, true, false, true, ct);
        return ApplicationCatalog.Resolve(snapshot, identity);
    }

    private async Task<ApplicationCatalogSnapshot> ReadCatalogAsync(int user, bool system, bool icons, bool refresh, CancellationToken ct)
    {
        await _catalogGate.WaitAsync(ct);
        try
        {
            var session = CatalogSession();
            if (!refresh && _catalogCache.TryGetValue((user, system, icons), out var cached) && cached.Session == session &&
                DateTimeOffset.UtcNow - cached.ObservedAt < TimeSpan.FromSeconds(30)) return cached;
            var instanceId = ReadInstanceId();
            var helper = await EnsureCatalogHelperAsync(session, ct);
            RequireCatalogSession(session);
            Progress.Value?.Invoke(new { stage = "reading_application_metadata", session, userId = user });
            var script = "CLASSPATH=" + Q(helper) + " app_process / dev.rgvm.catalog.Main " + user + " " +
                system.ToString().ToLowerInvariant() + " " + icons.ToString().ToLowerInvariant();
            string raw;
            try { raw = await ShellAsync(script, true, ct); }
            catch (DebugException error) when (MetadataTransportPolicy.CanRetryRead(error))
            {
                ct.ThrowIfCancellationRequested(); RequireCatalogSession(session);
                Progress.Value?.Invoke(new { stage = "retrying_application_metadata", session, attempt = 2, reason = "empty_adb_transport_exit", toolEvidencePath = error.Data["toolEvidencePath"] });
                if ((await AdbAsync(["get-state"], ct)).Trim() != "device") throw;
                RequireCatalogSession(session);
                raw = await ShellAsync(script, true, ct); // A metadata read is safe to repeat; mutation commands never use this path.
            }
            RequireCatalogSession(session);
            using var document = JsonDocument.Parse(raw);
            var result = document.RootElement;
            if (result.GetProperty("schemaVersion").GetInt32() != 1 || result.GetProperty("userId").GetInt32() != user)
                throw new DebugException("catalog_protocol", "应用元数据协议或用户不匹配。", "listing_applications");
            var rows = result.GetProperty("entries");
            if (rows.GetArrayLength() > 10000) throw new DebugException("catalog_limit", "应用数量超过清单容量，未返回截断清单。", "listing_applications");
            var applications = new List<CatalogApplication>();
            foreach (var row in rows.EnumerateArray())
            {
                var package = row.GetProperty("package").GetString()!;
                AndroidPackageName.Parse(package);
                if (row.GetProperty("userId").GetInt32() != user) throw new InvalidDataException("应用条目用户不匹配。");
                var uid = row.GetProperty("uid").GetInt32();
                var version = row.GetProperty("versionCode").GetInt64();
                var revision = ApplicationCatalog.Revision(package, user, uid, row.GetProperty("firstInstallTime").GetInt64(),
                    row.GetProperty("lastUpdateTime").GetInt64(), version, CatalogText(row, "sourceDir") ?? "");
                var identity = new ApplicationReference(instanceId, user, package, revision);
                var icon = icons ? SaveCatalogIcon(CatalogText(row, "iconPng")) : null;
                applications.Add(new(ApplicationCatalog.Reference(identity), package, CatalogText(row, "name") ?? package,
                    CatalogText(row, "nameSource") ?? "package_fallback", user, uid, CatalogText(row, "versionName") ?? "", version,
                    row.GetProperty("system").GetBoolean(), row.GetProperty("enabled").GetBoolean(), revision,
                    row.GetProperty("runningPids").EnumerateArray().Select(pid => pid.GetInt32()).ToArray(),
                    CatalogText(row, "runningStateError"), CatalogText(row, "privateDirectory"), CatalogText(row, "credentialProtectedDirectory"),
                    CatalogText(row, "deviceProtectedDirectory"), icon, CatalogText(row, "labelError") ?? CatalogText(row, "iconError")));
            }
            var snapshot = new ApplicationCatalogSnapshot(instanceId, session, user, CatalogText(result, "locale") ?? "",
                DateTimeOffset.UtcNow, applications.ToArray(), Guid.NewGuid().ToString("N"));
            _catalogCache[(user, system, icons)] = snapshot;
            foreach (var key in _catalogCache.Where(entry => entry.Value.Session != session).Select(entry => entry.Key).ToArray()) _catalogCache.Remove(key);
            return snapshot;
        }
        finally { _catalogGate.Release(); }
    }

    private static string? CatalogText(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private string? SaveCatalogIcon(string? encoded)
    {
        if (encoded is null) return null;
        if (encoded.Length > 87384) throw new InvalidDataException("应用图标超过64KiB。");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != 48 ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) != 48) throw new InvalidDataException("应用图标格式或尺寸无效。");
        var directory = Path.Combine(Paths.ProductRoot, "debug-runs", "catalog", "icons");
        StoragePathPolicy.RejectReparsePoints(directory);
        ColdCheckpoint.Restrict(directory);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".png");
        StoragePathPolicy.RejectReparsePoints(path);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        ColdCheckpoint.RestrictFile(path);
        return path;
    }

    private async Task<string> EnsureCatalogHelperAsync(string session, CancellationToken ct)
    {
        await _helperGate.WaitAsync(ct);
        try { return await EnsureCatalogHelperCoreAsync(session, ct); }
        finally { _helperGate.Release(); }
    }
    private async Task<string> EnsureCatalogHelperCoreAsync(string session, CancellationToken ct)
    {
        var assembly = typeof(AndroidDebugService).Assembly;
        using var manifestStream = assembly.GetManifestResourceStream("RootedAndroidGameVM.catalog-manifest.json")!;
        using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
        var digest = manifest.RootElement.GetProperty("dexSha256").GetString()!;
        using var resource = assembly.GetManifestResourceStream("RootedAndroidGameVM.catalog.dex")!;
        using var memory = new MemoryStream(); await resource.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != digest) throw new InvalidDataException("内置应用元数据程序散列不一致。");
        var remoteRoot = "/data/local/tmp/rgvm-catalog";
        var remote = remoteRoot + "/" + digest + ".dex";
        RequireCatalogSession(session);
        var present = await ShellAsync("test ! -L " + Q(remoteRoot) + " && test ! -L " + Q(remote) + " && test -f " + Q(remote) +
            " && sha256sum " + Q(remote) + " || true", true, ct);
        if (present.TrimStart().StartsWith(digest + " ", StringComparison.Ordinal)) return remote;
        var directory = Path.Combine(Paths.ProductRoot, "debug-runs", "catalog", "helper");
        StoragePathPolicy.RejectReparsePoints(directory); ColdCheckpoint.Restrict(directory);
        var local = Path.Combine(directory, digest + ".dex");
        StoragePathPolicy.RejectReparsePoints(local); await File.WriteAllBytesAsync(local, bytes, ct); ColdCheckpoint.RestrictFile(local);
        var transit = "/data/local/tmp/rgvm-catalog-transfer-" + Guid.NewGuid().ToString("N");
        try
        {
            RequireCatalogSession(session);
            await AdbAsync(["push", local, transit], ct);
            RequireCatalogSession(session);
            await ShellAsync("set -e; umask 077; test ! -L " + Q(remoteRoot) + "; mkdir -p " + Q(remoteRoot) +
                "; chown 0:0 " + Q(remoteRoot) + "; chmod 700 " + Q(remoteRoot) + "; test ! -L " + Q(remote) +
                "; test \"$(sha256sum " + Q(transit) + " | cut -d' ' -f1)\" = " + Q(digest) +
                "; cp " + Q(transit) + " " + Q(remote) + "; chmod 444 " + Q(remote), true, ct);
            return remote;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { RequireCatalogSession(session); await ShellAsync("rm -f " + Q(transit), true, cleanup.Token); }
            catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(directory, "cleanup-required.json"), DebugJson.Write(new { session, transit, error = error.Message }), CancellationToken.None); }
        }
    }
}
