using System.Formats.Tar;
using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Android;

public sealed class AndroidPrivateDataService
{
    private readonly AndroidSdkLayout _layout;
    private readonly AndroidVmOptions _options;
    private readonly IProcessRunner _runner;

    public AndroidPrivateDataService(
        AndroidSdkLayout? layout = null,
        AndroidVmOptions? options = null,
        IProcessRunner? runner = null)
    {
        _layout = layout ?? AndroidSdkLayout.Discover();
        _options = options ?? AndroidVmOptions.Default;
        _runner = runner ?? new ProcessRunner();
    }

    public async Task<string> ExportDirectoryAsync(
        string packageName,
        string relativePath,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        var package = AndroidPackageName.Parse(packageName);
        var dataPath = AndroidRelativePath.Parse(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        Directory.CreateDirectory(destinationRoot);

        var operationId = Guid.NewGuid().ToString("N");
        var exportName = $"{package.Value}-{DateTime.Now:yyyyMMdd-HHmmss}-{operationId[..8]}";
        var exportDirectory = PathBoundary.EnsureWithinRoot(
            destinationRoot,
            Path.Combine(destinationRoot, exportName));
        Directory.CreateDirectory(exportDirectory);

        var archiveName = $"rgvm-{operationId}.tar";
        var remoteArchive = $"/data/local/tmp/{archiveName}";
        var localArchive = PathBoundary.EnsureWithinRoot(exportDirectory, Path.Combine(exportDirectory, archiveName));
        var sourceRoot = $"/data/data/{package.Value}";
        var createScript = $"tar -C {sourceRoot} -cf {remoteArchive} {dataPath.Value}";

        var completed = false;
        try
        {
            EnsureSuccess(await _runner.RunAsync(
                AndroidCommandFactory.Adb(_layout, _options, "shell", "su", "-c", createScript),
                cancellationToken), "打包应用私有数据");
            EnsureSuccess(await _runner.RunAsync(
                AndroidCommandFactory.Adb(_layout, _options, "pull", remoteArchive, localArchive),
                cancellationToken), "复制应用私有数据");

            TarFile.ExtractToDirectory(localArchive, exportDirectory, overwriteFiles: false);
            completed = true;
            return PathBoundary.EnsureWithinRoot(exportDirectory, Path.Combine(exportDirectory, dataPath.Value));
        }
        finally
        {
            if (File.Exists(localArchive))
            {
                File.Delete(localArchive);
            }

            if (!completed && Directory.Exists(exportDirectory))
            {
                Directory.Delete(exportDirectory, recursive: true);
            }

            try
            {
                await _runner.RunAsync(
                    AndroidCommandFactory.Adb(_layout, _options, "shell", "su", "-c", $"rm -f {remoteArchive}"),
                    CancellationToken.None);
            }
            catch
            {
                // Cleanup is best effort; the randomized file contains no credentials and lives in /data/local/tmp.
            }
        }
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException($"{operation}失败：{detail}");
    }
}
