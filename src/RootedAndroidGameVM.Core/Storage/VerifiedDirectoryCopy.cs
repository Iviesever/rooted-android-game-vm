using System.Security.AccessControl;
using System.Security.Cryptography;
using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Security;

namespace RootedAndroidGameVM.Core.Storage;

public sealed record ResourceTransferProgress(string Stage, long CompletedBytes, long TotalBytes, string Detail);
public sealed record ResourceFile(string RelativePath, long Length, DateTime LastWriteTimeUtc, string Sha256 = "");
public sealed record ResourceInventory(List<ResourceFile> Files, List<string> Directories)
{
    public long TotalBytes => Files.Sum(file => file.Length);
}

public sealed record VerifiedCopyManifest(string SourceRoot, string DestinationRoot, ResourceInventory Inventory)
{
    public List<ResourceFile> Files => Inventory.Files;
}

public sealed class VerifiedDirectoryCopy
{
    public static ResourceInventory ReadInventory(string root, IReadOnlySet<string>? excludedRootNames = null)
    {
        StoragePathPolicy.RejectReparsePoints(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("原资源目录不存在。");
        var files = new List<ResourceFile>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(root, entry);
                if (directory == root && excludedRootNames?.Contains(Path.GetFileName(entry)) == true) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"资源包含目录或文件链接，无法安全迁移：{relative}");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(relative);
                    pending.Push(entry);
                }
                else
                {
                    var info = new FileInfo(entry);
                    files.Add(new ResourceFile(relative, info.Length, info.LastWriteTimeUtc));
                }
            }
        }
        return new ResourceInventory(files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(), directories);
    }

    public Task<VerifiedCopyManifest> CopyAsync(
        string source, string destination, IReadOnlySet<string>? excludedRootNames = null,
        IProgress<ResourceTransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => CopyCoreAsync(source, destination, excludedRootNames, progress, cancellationToken), cancellationToken);

    private static async Task<VerifiedCopyManifest> CopyCoreAsync(
        string source, string destination, IReadOnlySet<string>? excludedRootNames,
        IProgress<ResourceTransferProgress>? progress, CancellationToken cancellationToken)
    {
        source = StoragePathPolicy.NormalizeRoot(source);
        destination = StoragePathPolicy.NormalizeRoot(destination);
        if (StoragePathPolicy.Contains(source, destination) || StoragePathPolicy.Contains(destination, source))
            throw new ArgumentException("原目录与目标目录必须相互独立，不能互相包含。");
        StoragePathPolicy.RejectReparsePoints(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(destination) || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()))
            throw new IOException("目标文件夹必须为空，不会覆盖已有文件。");
        var inventory = ReadInventory(source, excludedRootNames);
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (drive.AvailableFreeSpace < inventory.TotalBytes + 512L * 1024 * 1024)
            throw new IOException("目标磁盘空间不足，请预留资源大小及至少 512 MB 余量。");
        Directory.CreateDirectory(destination);
        CopyDirectoryPermissions(source, destination);
        foreach (var relative in inventory.Directories.OrderBy(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetDirectory = PathBoundary.EnsureWithinRoot(destination, Path.Combine(destination, relative));
            Directory.CreateDirectory(targetDirectory);
            CopyDirectoryPermissions(Path.Combine(source, relative), targetDirectory);
        }

        long completedBytes = 0;
        var verifiedFiles = new List<ResourceFile>();
        var buffer = new byte[1024 * 1024];
        foreach (var file in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = PathBoundary.EnsureWithinRoot(source, Path.Combine(source, file.RelativePath));
            var destinationFile = PathBoundary.EnsureWithinRoot(destination, Path.Combine(destination, file.RelativePath));
            StoragePathPolicy.RejectReparsePoints(sourceFile);
            StoragePathPolicy.RejectReparsePoints(Path.GetDirectoryName(destinationFile)!);
            string expectedHash;
            long bytesWritten = 0;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(destinationFile, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    int length;
                    while ((length = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hasher.AppendData(buffer, 0, length);
                        await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                        bytesWritten += length;
                        completedBytes += length;
                        progress?.Report(new("正在复制资源", completedBytes, inventory.TotalBytes, file.RelativePath));
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                expectedHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }
            var sourceInfo = new FileInfo(sourceFile);
            if (bytesWritten != file.Length || sourceInfo.Length != file.Length || sourceInfo.LastWriteTimeUtc != file.LastWriteTimeUtc)
                throw new IOException($"复制期间原文件发生变化，已保留原目录：{file.RelativePath}");
            progress?.Report(new("正在校验副本", completedBytes, inventory.TotalBytes, file.RelativePath));
            var actualHash = await Sha256Verifier.ComputeAsync(destinationFile, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"副本校验失败，已保留原目录：{file.RelativePath}");
            File.SetLastWriteTimeUtc(destinationFile, file.LastWriteTimeUtc);
            File.SetAttributes(destinationFile, File.GetAttributes(sourceFile) &
                (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
            verifiedFiles.Add(file with { Sha256 = expectedHash });
        }
        return new(source, destination, new ResourceInventory(verifiedFiles, inventory.Directories));
    }

    private static void CopyDirectoryPermissions(string source, string destination)
    {
        if (!OperatingSystem.IsWindows()) return;
        var permissions = new DirectoryInfo(source).GetAccessControl(AccessControlSections.Access);
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        new DirectoryInfo(destination).SetAccessControl(permissions);
    }
}
