using System.IO.Compression;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Security;

namespace RootedAndroidGameVM.Core.Setup;

public sealed class ProductArchiveInstaller(
    VerifiedDownloader downloader,
    DirectoryMoveService? directoryMover = null)
{
    private readonly DirectoryMoveService _directoryMover = directoryMover ?? new();

    public async Task InstallAsync(
        DependencyComponent component,
        string downloadCache,
        string installRoot,
        string? archiveTopLevelDirectory,
        string targetRelativeDirectory,
        string revisionFileRelativePath,
        string revisionProperty,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = Path.GetFullPath(installRoot);
        var target = PathBoundary.EnsureWithinRoot(
            normalizedRoot,
            Path.Combine(normalizedRoot, targetRelativeDirectory));
        if (HasRevision(target, revisionFileRelativePath, revisionProperty, expectedRevision))
        {
            return;
        }

        Directory.CreateDirectory(normalizedRoot);
        Directory.CreateDirectory(downloadCache);
        var archivePath = PathBoundary.EnsureWithinRoot(
            downloadCache,
            Path.Combine(downloadCache, component.ArchiveFileName));
        var cached = File.Exists(archivePath) &&
                     (component.Size <= 0 || new FileInfo(archivePath).Length == component.Size) &&
                     string.Equals(
                         await Sha256Verifier.ComputeAsync(archivePath, cancellationToken),
                         component.Sha256,
                         StringComparison.OrdinalIgnoreCase);
        if (!cached)
        {
            await downloader.DownloadAsync(
                new Uri(component.Url),
                archivePath,
                component.Sha256,
                cancellationToken);
        }
        if (component.Size > 0 && new FileInfo(archivePath).Length != component.Size)
        {
            throw new InvalidDataException($"Archive '{component.Id}' size mismatch.");
        }

        var stagingRoot = PathBoundary.EnsureWithinRoot(
            normalizedRoot,
            Path.Combine(normalizedRoot, ".rgvm-staging"));
        Directory.CreateDirectory(stagingRoot);
        var staging = PathBoundary.EnsureWithinRoot(
            stagingRoot,
            Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging);
            var source = archiveTopLevelDirectory is null
                ? Directory.GetDirectories(staging).Single()
                : PathBoundary.EnsureWithinRoot(
                    staging,
                    Path.Combine(staging, archiveTopLevelDirectory));
            if (!Directory.Exists(source) ||
                !HasRevision(source, revisionFileRelativePath, revisionProperty, expectedRevision))
            {
                throw new InvalidDataException(
                    $"Archive '{component.Id}' does not contain the expected revision {expectedRevision}.");
            }

            var backupRoot = PathBoundary.EnsureWithinRoot(
                normalizedRoot,
                Path.Combine(normalizedRoot, ".rgvm-backup"));
            Directory.CreateDirectory(backupRoot);
            var backup = PathBoundary.EnsureWithinRoot(
                backupRoot,
                Path.Combine(backupRoot, Guid.NewGuid().ToString("N")));
            var hasBackup = false;
            if (Directory.Exists(target))
            {
                await _directoryMover.MoveAsync(target, backup, cancellationToken);
                hasBackup = true;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await _directoryMover.MoveAsync(source, target, cancellationToken);
            }
            catch
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }
                if (hasBackup && Directory.Exists(backup))
                {
                    await _directoryMover.MoveAsync(backup, target, CancellationToken.None);
                }
                throw;
            }
            if (hasBackup && Directory.Exists(backup))
            {
                Directory.Delete(backup, true);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    private static bool HasRevision(
        string directory,
        string revisionFileRelativePath,
        string revisionProperty,
        string expectedRevision)
    {
        var revisionFile = Path.Combine(directory, revisionFileRelativePath);
        if (!File.Exists(revisionFile)) return false;
        var prefix = revisionProperty + "=";
        var value = File.ReadLines(revisionFile)
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
            ?[prefix.Length..]
            .Trim()
            .Trim('"');
        return string.Equals(value, expectedRevision, StringComparison.Ordinal);
    }
}
