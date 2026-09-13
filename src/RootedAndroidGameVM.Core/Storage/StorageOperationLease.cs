namespace RootedAndroidGameVM.Core.Storage;

public sealed class StorageOperationLease : IDisposable
{
    public const string FileName = "storage-operation.lock";
    private readonly FileStream _stream;

    private StorageOperationLease(FileStream stream) => _stream = stream;

    public static StorageOperationLease Acquire(ProductStorageLocation? location = null, bool allowRecovery = false)
    {
        location ??= new ProductStorageLocation();
        StoragePathPolicy.RejectReparsePoints(location.ControlRoot);
        Directory.CreateDirectory(location.ControlRoot);
        FileStream stream;
        try
        {
            stream = new FileStream(Path.Combine(location.ControlRoot, FileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("另一项安装或资源操作正在进行，请等待完成后重试。", exception);
        }
        try
        {
            if (!allowRecovery && MigrationJournal.Read(location) is { Stage: not MigrationStage.Cleaning })
                throw new InvalidOperationException("上次资源迁移尚未完成，请打开“资源位置”恢复迁移。");
            return new StorageOperationLease(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _stream.Dispose();
}
