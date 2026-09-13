using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Storage;

public sealed class ResourceRemovalService
{
    private readonly ProductStorageLocation _location;
    private readonly Func<InstallPaths, CancellationToken, Task> _stop;

    public ResourceRemovalService(ProductStorageLocation? location = null, Func<InstallPaths, CancellationToken, Task>? stop = null)
    {
        _location = location ?? new ProductStorageLocation();
        _stop = stop ?? new AndroidStorageRuntime().StopAsync;
    }

    public Task RemoveAsync(string scope, CancellationToken cancellationToken = default) =>
        Task.Run(() => RemoveCoreAsync(scope, cancellationToken), cancellationToken);

    private async Task RemoveCoreAsync(string scope, CancellationToken cancellationToken)
    {
        if (scope is not ("runtime" or "all")) throw new ArgumentException("未知的资源卸载范围。", nameof(scope));
        using var lease = StorageOperationLease.Acquire(_location);
        if (MigrationJournal.Read(_location) is not null)
            throw new InvalidOperationException("请先完成资源迁移或恢复，再卸载资源。");
        var root = _location.ReadRoot();
        StorageOwnership.AssertSafeRoot(root, _location.ControlRoot);
        if (!StorageOwnership.IsOwned(root)) throw new InvalidDataException("无法确认产品资源目录，未删除任何资源。");
        var paths = InstallPaths.FromProductRoot(root);
        await _stop(paths, cancellationToken).ConfigureAwait(false);
        await StorageOwnership.MarkOwnedAsync(root, cancellationToken).ConfigureAwait(false);
        if (scope == "runtime")
        {
            foreach (var directory in new[] { paths.RuntimeRoot, Path.Combine(root, ".rgvm-staging"), Path.Combine(root, ".rgvm-backup") })
            {
                PathBoundary.EnsureWithinRoot(root, directory);
                if (Directory.Exists(directory)) DeleteContents(directory, null, removeRoot: true);
            }
            foreach (var name in new[] { "install.json", "install-state.json" }) DeleteFile(Path.Combine(root, name));
            return;
        }
        DeleteContents(root, StorageOwnership.ControlFiles,
            removeRoot: !string.Equals(root, _location.ControlRoot, StringComparison.OrdinalIgnoreCase));
        DeleteFile(_location.LocationFilePath);
        DeleteFile(Path.Combine(_location.ControlRoot, ResourceMigrationService.ReceiptFileName));
    }

    private static void DeleteContents(string root, IReadOnlySet<string>? exclusions, bool removeRoot)
    {
        var inventory = VerifiedDirectoryCopy.ReadInventory(root, exclusions);
        foreach (var file in inventory.Files.Where(file => file.RelativePath != StorageOwnership.MarkerName))
            DeleteFile(PathBoundary.EnsureWithinRoot(root, Path.Combine(root, file.RelativePath)));
        foreach (var relative in inventory.Directories.OrderByDescending(path => path.Length))
        {
            var directory = PathBoundary.EnsureWithinRoot(root, Path.Combine(root, relative));
            StoragePathPolicy.RejectReparsePoints(directory);
            if (Directory.Exists(directory)) Directory.Delete(directory); // Never follow or recursively remove an unobserved entry.
        }
        if (Directory.EnumerateFileSystemEntries(root).Any(path =>
                Path.GetFileName(path) != StorageOwnership.MarkerName && exclusions?.Contains(Path.GetFileName(path)) != true))
            throw new IOException("卸载期间出现新的文件，已保留目录标识，请重试。");
        DeleteFile(Path.Combine(root, StorageOwnership.MarkerName));
        if (removeRoot) Directory.Delete(root);
    }

    private static void DeleteFile(string path)
    {
        StoragePathPolicy.RejectReparsePoints(path);
        if (!File.Exists(path)) return;
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }
}
