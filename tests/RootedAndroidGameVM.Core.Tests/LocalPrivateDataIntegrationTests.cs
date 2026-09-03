using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class LocalPrivateDataIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task Existing_rooted_avd_exports_a_configured_private_directory()
    {
        if (Environment.GetEnvironmentVariable("RGVM_RUN_LOCAL_AVD_TESTS") != "1")
        {
            throw new InvalidOperationException("Set RGVM_RUN_LOCAL_AVD_TESTS=1 to run this local integration test.");
        }
        var packageName = Environment.GetEnvironmentVariable("RGVM_LOCAL_TEST_PACKAGE")
            ?? throw new InvalidOperationException("Set RGVM_LOCAL_TEST_PACKAGE to an installed application package.");
        var relativePath = Environment.GetEnvironmentVariable("RGVM_LOCAL_TEST_RELATIVE_PATH") ?? "files";

        var controller = new AndroidVmController();
        var dataService = new AndroidPrivateDataService();
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "RootedAndroidGameVM.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            await controller.StartAsync();
            Assert.Equal(VmStatus.Running, await controller.GetStatusAsync());
            Assert.Contains("Root：正常（uid=0）", await controller.DiagnoseAsync());

            var exportedPath = await dataService.ExportDirectoryAsync(
                packageName,
                relativePath,
                temporaryRoot);
            Assert.True(Directory.Exists(exportedPath));
        }
        finally
        {
            await controller.StopAsync();
            var expectedTestRoot = Path.Combine(Path.GetTempPath(), "RootedAndroidGameVM.Tests");
            Assert.StartsWith(
                Path.GetFullPath(expectedTestRoot),
                Path.GetFullPath(temporaryRoot),
                StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }
}
