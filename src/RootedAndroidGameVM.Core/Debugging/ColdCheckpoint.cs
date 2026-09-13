using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;
using RootedAndroidGameVM.Core.Processes;
using System.Runtime.Versioning;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record CheckpointFile(string Path, long Length, string Sha256);
public sealed record CheckpointManifest(int SchemaVersion, string Id, string DataRoot, string AvdName,
    DateTimeOffset CreatedAt, CheckpointFile[] Files, CheckpointFile[] Components, Dictionary<string, string> BackingChains);
public sealed record ColdRestoreResult(string Checkpoint, string Rollback, bool RequiresColdBootVerification = true);

[SupportedOSPlatform("windows")]
public sealed class ColdCheckpoint(InstallPaths paths, OwnedInstance instance)
{
    public string Root => Path.Combine(paths.ProductRoot, "checkpoints");
    public static string SafeChild(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is ".." or "." or "") || relative.Contains(':'))
            throw new ArgumentException("非法相对路径。");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路径越界。");
        StoragePathPolicy.RejectReparsePoints(path);
        return path;
    }
    public static void Restrict(string directory)
    {
        Directory.CreateDirectory(directory);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
    }
    public static void RestrictFile(string path)
    {
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
    public static async Task<CheckpointFile> DigestAsync(string root, string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        return new(Path.GetRelativePath(root, file), stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)));
    }
    public static void RequireSpace(string target, long bytes)
    {
        if (bytes < 0 || new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target))!).AvailableFreeSpace - 512L * 1024 * 1024 < bytes)
            throw new DebugException("disk_full", "空间不足；尚未改动原数据。");
    }
    private string[] PersistentFiles() => Directory.EnumerateFiles(paths.AvdHome, "*", SearchOption.AllDirectories)
        .Where(f => !Path.GetRelativePath(paths.AvdHome, f).Split(Path.DirectorySeparatorChar).Any(s =>
            s is "snapshots" or "tmp" || s.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
        .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).ToArray();
    private async Task<CheckpointFile[]> ComponentsAsync(CancellationToken ct)
    {
        var files = new List<string> { Path.Combine(paths.SdkRoot, "emulator", "emulator.exe"), Path.Combine(paths.SdkRoot, "emulator", "qemu-img.exe"),
            Path.Combine(paths.SdkRoot, "platform-tools", "adb.exe"),
            Path.Combine(paths.SdkRoot, "emulator", "source.properties"),
            Path.Combine(paths.SdkRoot, "emulator", "qemu", "windows-x86_64", "qemu-system-x86_64.exe"),
            Path.Combine(paths.SdkRoot, "emulator", "qemu", "windows-x86_64", "qemu-system-x86_64-headless.exe") };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(paths.SdkRoot, "system-images"), "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) == ".img" || Path.GetFileName(f) is "source.properties" or "package.xml" or "kernel-ranchu"));
        var result = new List<CheckpointFile>();
        foreach (var file in files) { StoragePathPolicy.RejectReparsePoints(file); result.Add(await DigestAsync(paths.ProductRoot, file, ct)); }
        return result.ToArray();
    }
    public async Task<CheckpointManifest> CreateAsync(CancellationToken ct)
    {
        instance.RequireStopped();
        if (Directory.GetDirectories(paths.AvdHome, "*.avd").Any(d => !string.Equals(d, instance.AvdDirectory, StringComparison.OrdinalIgnoreCase)))
            throw new DebugException("instance_mismatch", "AVD 目录含其他实例；拒绝整体磁盘检查点。");
        var files = PersistentFiles();
        foreach (var f in files) StoragePathPolicy.RejectReparsePoints(f);
        RequireSpace(Root, files.Sum(f => new FileInfo(f).Length));
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var staging = Path.Combine(Root, ".pending-" + id);
        Restrict(staging);
        // Failed staging is intentionally retained and reported, never silently treated as a checkpoint.
        var entries = new List<CheckpointFile>();
        var chains = new Dictionary<string, string>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested(); instance.RequireStopped();
            var rel = Path.GetRelativePath(paths.AvdHome, f);
            var destination = SafeChild(Path.Combine(staging, "avd"), rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var source = File.OpenRead(f))
            await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await source.CopyToAsync(target, ct); await target.FlushAsync(ct); target.Flush(true); }
            var original = await DigestAsync(paths.AvdHome, f, ct);
            var copied = await DigestAsync(Path.Combine(staging, "avd"), destination, ct);
            if (copied != original) throw new IOException("检查点复制校验失败。");
            entries.Add(original);
            if (f.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await BinaryProcess.RunAsync(new ProcessSpec(Path.Combine(paths.SdkRoot, "emulator", "qemu-img.exe"),
                    ["info", "--backing-chain", "--output=json", f]), 1024 * 1024, ct);
                var json = System.Text.Encoding.UTF8.GetString(bytes);
                using var chain = JsonDocument.Parse(json);
                foreach (var element in chain.RootElement.EnumerateArray())
                {
                    var filename = element.GetProperty("filename").GetString()!;
                    var absolute = Path.GetFullPath(filename, Path.GetDirectoryName(f)!);
                    if (!absolute.StartsWith(paths.ProductRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("磁盘 backing file 在产品资源目录之外，拒绝不完整检查点。");
                    StoragePathPolicy.RejectReparsePoints(absolute);
                }
                chains.Add(rel, json);
            }
        }
        var manifest = new CheckpointManifest(1, id, paths.ProductRoot, Path.GetFileNameWithoutExtension(instance.AvdDirectory),
            DateTimeOffset.UtcNow, entries.ToArray(), await ComponentsAsync(ct), chains);
        instance.RequireStopped();
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), DebugJson.Write(manifest), ct);
        Directory.Move(staging, Path.Combine(Root, id));
        return manifest;
    }
    public async Task<ColdRestoreResult> RestoreAsync(string id, CancellationToken ct)
    {
        instance.RequireStopped();
        var checkpoint = SafeChild(Root, id);
        var manifest = JsonSerializer.Deserialize<CheckpointManifest>(await File.ReadAllTextAsync(Path.Combine(checkpoint, "manifest.json"), ct), DebugJson.Options)
            ?? throw new IOException("检查点清单损坏。");
        if (manifest.SchemaVersion != 1 || manifest.Id != id || manifest.AvdName != Path.GetFileNameWithoutExtension(instance.AvdDirectory))
            throw new IOException("检查点版本或实例不兼容。");
        // Immutable checkpoint is never rewritten by migration. Relocate only the staged restore copy.
        var components = await ComponentsAsync(ct);
        if (!components.OrderBy(f => f.Path).SequenceEqual(manifest.Components.OrderBy(f => f.Path))) throw new IOException("运行组件、Root ramdisk 或系统镜像已变化；拒绝恢复。");
        foreach (var item in manifest.Files)
        {
            var path = SafeChild(Path.Combine(checkpoint, "avd"), item.Path);
            if (await DigestAsync(Path.Combine(checkpoint, "avd"), path, ct) != item) throw new IOException("检查点文件校验失败：" + item.Path);
        }
        RequireSpace(paths.AvdHome, manifest.Files.Sum(f => f.Length));
        var stage = paths.AvdHome + ".restore-" + Guid.NewGuid().ToString("N");
        Restrict(stage);
        foreach (var item in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var dest = SafeChild(stage, item.Path); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using var input = File.OpenRead(SafeChild(Path.Combine(checkpoint, "avd"), item.Path));
            await using var output = File.Create(dest); await input.CopyToAsync(output, ct);
        }
        if (!string.Equals(manifest.DataRoot, paths.ProductRoot, StringComparison.OrdinalIgnoreCase))
            await RelocateStagedAsync(stage, manifest, ct);
        instance.RequireStopped(); ct.ThrowIfCancellationRequested();
        var rollback = paths.AvdHome + ".before-restore-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        await File.WriteAllTextAsync(Path.Combine(paths.ProductRoot, "debug-restore.json"), DebugJson.Write(new { checkpoint = id, current = paths.AvdHome, stage, rollback }), ct);
        SwitchDirectories(paths.AvdHome, stage, rollback);
        return new(id, rollback);
    }
    public void CompleteRestore() => File.Delete(Path.Combine(paths.ProductRoot, "debug-restore.json"));
    public bool HasPendingRestore => File.Exists(Path.Combine(paths.ProductRoot, "debug-restore.json"));
    public async Task<object> RecoverPendingAsync(CancellationToken ct)
    {
        instance.RequireStopped();
        if (!HasPendingRestore) return new { recovered = false, reason = "no_pending_restore" };
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.ProductRoot, "debug-restore.json"), ct));
        var current = doc.RootElement.GetProperty("current").GetString()!;
        var rollback = doc.RootElement.GetProperty("rollback").GetString()!;
        if (current != paths.AvdHome || !rollback.StartsWith(paths.AvdHome + ".before-restore-", StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复日志路径无效。");
        StoragePathPolicy.RejectReparsePoints(rollback);
        if (Directory.Exists(rollback))
        {
            if (Directory.Exists(current)) SwitchDirectories(current, rollback, paths.AvdHome + ".interrupted-restore-" + Guid.NewGuid().ToString("N"));
            else Directory.Move(rollback, current);
        }
        else if (!Directory.Exists(current)) throw new IOException("当前目录与回退目录均缺失，需要人工恢复。");
        CompleteRestore(); return new { recovered = true, originalStateRetained = true };
    }
    public void Rollback(ColdRestoreResult result)
    {
        instance.RequireStopped();
        if (!result.Rollback.StartsWith(paths.AvdHome + ".before-restore-", StringComparison.OrdinalIgnoreCase)) throw new IOException("无效回退目录。");
        StoragePathPolicy.RejectReparsePoints(result.Rollback);
        SwitchDirectories(paths.AvdHome, result.Rollback, paths.AvdHome + ".failed-restore-" + Guid.NewGuid().ToString("N"));
        CompleteRestore();
    }
    public static void SwitchDirectories(string current, string stage, string rollback, Action<string, string>? move = null)
    {
        move ??= Directory.Move;
        move(current, rollback);
        try { move(stage, current); }
        catch { move(rollback, current); throw; }
    }
    private async Task RelocateStagedAsync(string stage, CheckpointManifest manifest, CancellationToken ct)
    {
        var old = InstallPaths.FromProductRoot(manifest.DataRoot);
        foreach (var f in Directory.EnumerateFiles(stage, "*.ini", SearchOption.AllDirectories))
        {
            var value = await File.ReadAllTextAsync(f, ct);
            await File.WriteAllTextAsync(f, value.Replace(old.ProductRoot, paths.ProductRoot, StringComparison.OrdinalIgnoreCase), ct);
        }
        var qemuImg = Path.Combine(paths.SdkRoot, "emulator", "qemu-img.exe");
        foreach (var f in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".img") || f.EndsWith(".qcow2")))
        {
            using var doc = JsonDocument.Parse(await BinaryProcess.RunAsync(new(qemuImg, ["info", "--output=json", f]), 1024 * 1024, ct));
            if (!doc.RootElement.TryGetProperty("backing-filename", out var backing)) continue;
            var value = backing.GetString()!; if (!Path.IsPathFullyQualified(value)) continue;
            var rel = Path.GetRelativePath(old.ProductRoot, value); var target = SafeChild(paths.ProductRoot, rel);
            var avdRelative = Path.GetRelativePath(old.AvdHome, value);
            var basis = StoragePathPolicy.Contains(old.AvdHome, value) ? SafeChild(stage, avdRelative) : target;
            if (!File.Exists(basis)) throw new IOException("迁移后的 backing-file 缺失。");
            // Bases were verified against immutable checkpoint files/components before rebasing this staged copy.
            var format = doc.RootElement.GetProperty("backing-filename-format").GetString()!;
            if (format is not ("raw" or "qcow2")) throw new IOException("未知 backing 格式。");
            await BinaryProcess.RunAsync(new(qemuImg, ["rebase", "-u", "-f", "qcow2", "-F", format, "-b", target, f]), 1024 * 1024, ct);
        }
    }
    public object List() => !Directory.Exists(Root) ? Array.Empty<object>() : Directory.GetDirectories(Root)
        .Where(d => File.Exists(Path.Combine(d, "manifest.json"))).Select(d => new { id = Path.GetFileName(d), path = d }).ToArray();
}
