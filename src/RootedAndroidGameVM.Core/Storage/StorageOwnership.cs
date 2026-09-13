using System.Text.Json;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Storage;

public static class StorageOwnership
{
    public const string MarkerName = ".rgvm-storage.json";
    public const string ProductId = "2B456CBE-77EC-4F4B-911A-32D78A42F287";
    public static readonly IReadOnlySet<string> ControlFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ProductStorageLocation.LocationFileName, MigrationJournal.FileName, StorageOperationLease.FileName,
        ResourceMigrationService.ReceiptFileName
    };

    public static void AssertSafeRoot(string root, string controlRoot)
    {
        root = StoragePathPolicy.NormalizeRoot(root);
        StoragePathPolicy.RejectReparsePoints(root);
        if ((!string.Equals(root, controlRoot, StringComparison.OrdinalIgnoreCase) && StoragePathPolicy.Contains(root, controlRoot)) ||
            StoragePathPolicy.Contains(root, AppContext.BaseDirectory) || StoragePathPolicy.Contains(AppContext.BaseDirectory, root))
            throw new InvalidOperationException("请选择独立资源目录，不能包含配置目录或程序目录。");
    }

    public static bool IsOwned(string root)
    {
        StoragePathPolicy.RejectReparsePoints(root);
        var marker = Path.Combine(root, MarkerName);
        if (File.Exists(marker))
        {
            StoragePathPolicy.RejectReparsePoints(marker);
            var document = JsonSerializer.Deserialize<OwnerDocument>(File.ReadAllText(marker), AtomicJsonFile.Options);
            return document is { SchemaVersion: 1 } && document.ProductId == ProductId;
        }
        var paths = InstallPaths.FromProductRoot(root);
        foreach (var name in new[] { "install.json", "install-state.json" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            StoragePathPolicy.RejectReparsePoints(path);
            var document = JsonSerializer.Deserialize<LegacyInstall>(File.ReadAllText(path), AtomicJsonFile.Options);
            if (document?.AvdName == "rooted_android_game_vm_api35" &&
                string.Equals(document.SdkRoot, paths.SdkRoot, StringComparison.OrdinalIgnoreCase) &&
                (document.AvdHome is null || string.Equals(document.AvdHome, paths.AvdHome, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    public static async Task InitializeAsync(string root, ProductStorageLocation location, CancellationToken cancellationToken = default)
    {
        root = StoragePathPolicy.NormalizeRoot(root);
        AssertSafeRoot(root, location.ControlRoot);
        var current = location.ReadRoot();
        if (!string.Equals(root, current, StringComparison.OrdinalIgnoreCase) && Directory.Exists(current) && IsOwned(current))
            throw new InvalidOperationException("已有模拟器资源。更新会沿用原位置；如需更换位置，请在启动器中使用“迁移资源”。");
        if (Directory.Exists(root) && VerifiedDirectoryCopy.ReadInventory(root, ControlFiles) is var inventory &&
            (inventory.Files.Count > 0 || inventory.Directories.Count > 0) && !IsOwned(root))
            throw new IOException("这个文件夹已有其他文件，请为模拟器选择一个独立的空文件夹。");
        Directory.CreateDirectory(root);
        await MarkOwnedAsync(root, cancellationToken);
        await location.SaveRootAsync(root, cancellationToken);
    }

    internal static Task MarkOwnedAsync(string root, CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(Path.Combine(root, MarkerName), new OwnerDocument(1, ProductId), cancellationToken);

    private sealed record OwnerDocument(int SchemaVersion, string ProductId);
    private sealed record LegacyInstall(string? SdkRoot, string? AvdHome, string? AvdName);
}
