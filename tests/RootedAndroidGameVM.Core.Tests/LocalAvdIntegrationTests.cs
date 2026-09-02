using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class LocalAvdIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task Existing_rooted_avd_boots_and_exports_real_arcaea_aff_files()
    {
        if (Environment.GetEnvironmentVariable("RGVM_RUN_LOCAL_AVD_TESTS") != "1")
        {
            throw new InvalidOperationException("Set RGVM_RUN_LOCAL_AVD_TESTS=1 to run this local integration test.");
        }

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

            var diagnostics = await controller.DiagnoseAsync();
            Assert.Contains("Root：正常（uid=0）", diagnostics);

            var exportedPath = await dataService.ExportDirectoryAsync(
                "moe.low.arc",
                "files/dl",
                temporaryRoot);
            Assert.True(Directory.Exists(exportedPath));
            Assert.Contains(
                Directory.EnumerateFiles(exportedPath, "*", SearchOption.AllDirectories),
                LooksLikeAffChart);
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

    private static bool LooksLikeAffChart(string path)
    {
        if (new FileInfo(path).Length > 1_000_000) return false;
        try
        {
            using var reader = new StreamReader(path);
            var prefix = new char[512];
            var count = reader.Read(prefix, 0, prefix.Length);
            var text = new string(prefix, 0, count);
            return text.StartsWith("AudioOffset:", StringComparison.Ordinal) &&
                   text.Contains("timing(", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
