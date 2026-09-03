using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class CleanInstallIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task Magisk_additional_setup_and_shell_policy_are_automated()
    {
        if (Environment.GetEnvironmentVariable("RGVM_RUN_MAGISK_POLICY_TESTS") != "1")
        {
            throw new InvalidOperationException("Set RGVM_RUN_MAGISK_POLICY_TESTS=1 to run this local integration test.");
        }

        var paths = InstallPaths.FromProductRoot(
            @"D:\program\Magisk\RootedAndroidGameVM\artifacts\clean-e2e-runtime");
        var layout = AndroidSdkLayout.FromRoot(paths.SdkRoot);
        var options = new AndroidVmOptions(
            "rgvm_clean_test_api35",
            "emulator-5564",
            5564,
            "swiftshader_indirect",
            4096,
            paths.AvdHome);
        await new MagiskPolicyAutomator(layout, options).GrantShellAsync();

        Assert.Contains(
            "Root：正常（uid=0）",
            await new AndroidVmController(layout, options).DiagnoseAsync());
    }

    [Fact]
    [Trait("Category", "CleanE2E")]
    public async Task Fresh_isolated_sdk_avd_and_root_install_passes_end_to_end()
    {
        var productRoot = Environment.GetEnvironmentVariable("RGVM_CLEAN_INSTALL_ROOT");
        if (string.IsNullOrWhiteSpace(productRoot))
        {
            throw new InvalidOperationException("Set RGVM_CLEAN_INSTALL_ROOT to an isolated empty product root.");
        }
        if (Environment.GetEnvironmentVariable("RGVM_E2E_REUSE_STATE") != "1" &&
            Directory.Exists(productRoot) &&
            Directory.EnumerateFileSystemEntries(productRoot).Any())
        {
            throw new InvalidOperationException(
                "CleanE2E product root must be absent or empty unless RGVM_E2E_REUSE_STATE=1.");
        }

        var paths = InstallPaths.FromProductRoot(productRoot);
        var options = new AndroidVmOptions(
            "rgvm_clean_test_api35",
            "emulator-5564",
            5564,
            "swiftshader_indirect",
            4096,
            paths.AvdHome,
            Headless: Environment.GetEnvironmentVariable("RGVM_E2E_HEADLESS") == "1",
            Verbose: Environment.GetEnvironmentVariable("RGVM_E2E_HEADLESS") == "1");
        var states = new List<SetupProgressState>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        var installer = new RootedVmInstaller(
            paths,
            options: options);

        await installer.InstallAsync(
            sdkLicenseAccepted: true,
            new SynchronousProgress<SetupProgressState>(states.Add),
            timeout.Token,
            adoptExistingEnvironment: false);

        var layout = AndroidSdkLayout.FromRoot(paths.SdkRoot);
        var controller = new AndroidVmController(layout, options);
        try
        {
            Assert.Contains(states, state => state.Stage == SetupStage.Complete);
            Assert.Contains("Root：正常（uid=0）", await controller.DiagnoseAsync(timeout.Token));
            Assert.True(File.Exists(Path.Combine(paths.ProductRoot, "install.json")));
        }
        finally
        {
            await controller.StopAsync(CancellationToken.None);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
