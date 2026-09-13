using System.Text.Json;

namespace RootedAndroidGameVM.Core.Storage;

public sealed class ProductStorageLocation
{
    public const string LocationFileName = "storage-location.json";
    public string ControlRoot { get; }
    public string LocationFilePath => Path.Combine(ControlRoot, LocationFileName);

    public ProductStorageLocation(string? controlRoot = null)
    {
        ControlRoot = StoragePathPolicy.NormalizeRoot(controlRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RootedAndroidGameVM"));
    }

    public string ReadRoot()
    {
        if (!File.Exists(LocationFilePath)) return ControlRoot;
        try
        {
            var document = JsonSerializer.Deserialize<LocationDocument>(
                File.ReadAllText(LocationFilePath), AtomicJsonFile.Options);
            if (document is null || document.SchemaVersion != 1)
                throw new InvalidDataException("资源位置配置版本无效。");
            var root = StoragePathPolicy.NormalizeRoot(document.DataRoot);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"资源目录不可用：{root}。请连接对应磁盘或恢复原目录。");
            StoragePathPolicy.RejectReparsePoints(root);
            return root;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("无法读取资源位置配置，请恢复 storage-location.json；不会创建替代模拟器。", exception);
        }
    }

    public async Task SaveRootAsync(string root, CancellationToken cancellationToken = default)
    {
        var normalized = StoragePathPolicy.NormalizeRoot(root);
        StoragePathPolicy.RejectReparsePoints(normalized);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(normalized);
        await AtomicJsonFile.WriteAsync(LocationFilePath, new LocationDocument(1, normalized), cancellationToken);
    }

    private sealed record LocationDocument(int SchemaVersion, string DataRoot);
}
