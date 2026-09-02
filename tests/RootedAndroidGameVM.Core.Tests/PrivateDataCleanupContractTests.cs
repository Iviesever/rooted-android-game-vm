using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class PrivateDataCleanupContractTests
{
    [Fact]
    public async Task Failed_archive_extraction_leaves_no_private_tar_or_partial_export_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-private-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layout = AndroidSdkLayout.FromRoot(Path.Combine(root, "sdk"));
            var service = new AndroidPrivateDataService(layout, AndroidVmOptions.Default, new InvalidTarPullRunner());

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.ExportDirectoryAsync("com.example.game", "files", root));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InvalidTarPullRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            ProcessSpec spec,
            CancellationToken cancellationToken = default)
        {
            if (spec.Arguments.Contains("pull"))
            {
                File.WriteAllText(spec.Arguments[^1], "not a tar archive");
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
