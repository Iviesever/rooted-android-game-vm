using System.IO.Compression;
using System.Xml.Linq;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Security;

namespace RootedAndroidGameVM.Core.Setup;

public sealed class SdkArchiveInstaller(
    VerifiedDownloader downloader,
    DirectoryMoveService? directoryMover = null)
{
    private readonly DirectoryMoveService _directoryMover = directoryMover ?? new();

    public async Task InstallAsync(
        DependencyComponent component,
        string downloadCache,
        string sdkRoot,
        string archiveTopLevelDirectory,
        string targetRelativeDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.Sha256.Length != 64 || !component.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                $"SDK archive '{component.Id}' requires a product-side SHA-256 digest.");
        }

        var normalizedSdkRoot = Path.GetFullPath(sdkRoot);
        Directory.CreateDirectory(normalizedSdkRoot);
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
            throw new InvalidDataException(
                $"SDK archive '{component.Id}' size mismatch.");
        }

        var staging = PathBoundary.EnsureWithinRoot(
            normalizedSdkRoot,
            Path.Combine(normalizedSdkRoot, ".rgvm-staging", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging);
            var source = PathBoundary.EnsureWithinRoot(
                staging,
                Path.Combine(staging, archiveTopLevelDirectory));
            if (!Directory.Exists(source))
            {
                throw new InvalidDataException(
                    $"SDK archive '{component.Id}' does not contain '{archiveTopLevelDirectory}'.");
            }

            VerifyExtractedRevision(component, source);

            var target = PathBoundary.EnsureWithinRoot(
                normalizedSdkRoot,
                Path.Combine(normalizedSdkRoot, targetRelativeDirectory));
            var backupRoot = PathBoundary.EnsureWithinRoot(
                normalizedSdkRoot,
                Path.Combine(normalizedSdkRoot, ".rgvm-backup"));
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
                await EnsureGenericRegistrationAsync(
                    component,
                    normalizedSdkRoot,
                    targetRelativeDirectory,
                    cancellationToken);
            }
            catch
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
                if (hasBackup && Directory.Exists(backup))
                {
                    await _directoryMover.MoveAsync(backup, target, CancellationToken.None);
                }
                throw;
            }
            if (hasBackup && Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static void VerifyExtractedRevision(
        DependencyComponent component,
        string source)
    {
        var propertiesPath = Path.Combine(source, "source.properties");
        if (!File.Exists(propertiesPath))
        {
            throw new InvalidDataException(
                $"SDK archive '{component.Id}' is missing source.properties.");
        }
        var revision = File.ReadLines(propertiesPath)
            .FirstOrDefault(line => line.StartsWith("Pkg.Revision=", StringComparison.Ordinal))
            ?.Split('=', 2)[1]
            .Trim();
        if (!string.Equals(revision, component.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SDK archive '{component.Id}' revision mismatch. " +
                $"Expected {component.Version}, got {revision ?? "<missing>"}.");
        }
    }

    public async Task EnsureGenericRegistrationAsync(
        DependencyComponent component,
        string sdkRoot,
        string targetRelativeDirectory,
        CancellationToken cancellationToken = default)
    {
        var target = PathBoundary.EnsureWithinRoot(
            sdkRoot,
            Path.Combine(sdkRoot, targetRelativeDirectory));
        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException(
                $"SDK target '{targetRelativeDirectory}' is missing.");
        }
        var packageXml = Path.Combine(target, "package.xml");
        var expectedPath = targetRelativeDirectory
            .Replace(Path.DirectorySeparatorChar, ';')
            .Replace(Path.AltDirectorySeparatorChar, ';');
        var isSystemImage = expectedPath.StartsWith("system-images;", StringComparison.Ordinal);
        if (File.Exists(packageXml))
        {
            try
            {
                var localPackage = XDocument.Load(packageXml)
                    .Descendants()
                    .FirstOrDefault(element =>
                        element.Name.LocalName == "localPackage" &&
                        element.Name.Namespace == XNamespace.None &&
                        element.Attribute("path")?.Value == expectedPath);
                XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
                var typeName = localPackage?
                    .Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "type-details")?
                    .Attribute(xsi + "type")?
                    .Value;
                if (localPackage is not null &&
                    (!isSystemImage || typeName?.EndsWith(":sysImgDetailsType", StringComparison.Ordinal) == true))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
            {
                // Replace malformed generated metadata with the pinned local package descriptor.
            }
        }
        if (isSystemImage)
        {
            await AndroidLocalPackageMetadataWriter.WriteSystemImageAsync(
                packageXml,
                expectedPath,
                Path.Combine(target, "source.properties"),
                component,
                cancellationToken);
        }
        else
        {
            await AndroidLocalPackageMetadataWriter.WriteGenericAsync(
                packageXml,
                expectedPath,
                component,
                cancellationToken);
        }
    }
}
