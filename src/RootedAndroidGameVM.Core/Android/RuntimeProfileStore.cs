using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Android;

public sealed record RuntimeProfileChange(RuntimeProfile Previous, RuntimeProfile Requested, string Backup, bool RequiresRestart = true);

public sealed class RuntimeProfileStore(InstallPaths paths)
{
    public string ConfigPath => Path.Combine(paths.AvdHome, "rooted_android_game_vm_api35.avd", "config.ini");

    public RuntimeProfile Read()
    {
        StoragePathPolicy.RejectReparsePoints(ConfigPath);
        return File.Exists(ConfigPath) ? RuntimeProfile.FromAvdSettings(File.ReadLines(ConfigPath)) : RuntimeProfile.Recommended;
    }

    public async Task<RuntimeProfileChange> ApplyAsync(RuntimeProfile requested, Action requireStopped,
        long hostMemoryMb, int hostLogicalCores, CancellationToken cancellationToken = default)
    {
        requested.Validate(hostMemoryMb, hostLogicalCores);
        requireStopped();
        if (!StorageOwnership.IsOwned(paths.ProductRoot)) throw new InvalidOperationException("资源目录归属无法确认。");
        StoragePathPolicy.RejectReparsePoints(ConfigPath);
        var previous = Read();
        var backupRoot = Path.Combine(paths.ProductRoot, "runtime-settings-backups");
        StoragePathPolicy.RejectReparsePoints(backupRoot);
        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(backupRoot, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".ini");
        File.Copy(ConfigPath, backup);
        cancellationToken.ThrowIfCancellationRequested();
        requireStopped();
        await AvdConfigEditor.UpsertAsync(ConfigPath, requested.ToAvdSettings(), cancellationToken);
        return new(previous, requested, backup);
    }
}
