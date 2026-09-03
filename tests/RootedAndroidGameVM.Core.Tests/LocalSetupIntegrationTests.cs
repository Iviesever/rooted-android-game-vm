using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class LocalSetupIntegrationTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task Setup_adopts_and_records_the_existing_verified_rooted_avd()
    {
        if (Environment.GetEnvironmentVariable("RGVM_RUN_LOCAL_SETUP_TESTS") != "1")
        {
            throw new InvalidOperationException("Set RGVM_RUN_LOCAL_SETUP_TESTS=1 to run this local integration test.");
        }

        var states = new List<SetupProgressState>();
        var progress = new SynchronousProgress<SetupProgressState>(states.Add);
        var controller = new AndroidVmController();
        try
        {
            await new RootedVmInstaller().InstallAsync(
                true,
                progress,
                adoptExistingEnvironment: true);

            Assert.Contains(states, state => state.Stage == SetupStage.Complete && state.Percent == 100);
            Assert.Contains("Root：正常（uid=0）", await controller.DiagnoseAsync());
            Assert.True(File.Exists(Path.Combine(InstallPaths.CreateDefault().ProductRoot, "install.json")));
        }
        finally
        {
            await controller.StopAsync();
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
