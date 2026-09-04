using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class StrictVmStartupTests
{
    [Fact]
    public async Task Verification_start_rejects_an_existing_same_name_device_before_any_input()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-startup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "emulator"));
        Directory.CreateDirectory(Path.Combine(root, "platform-tools"));
        File.WriteAllText(Path.Combine(root, "emulator", "emulator.exe"), "placeholder");
        File.WriteAllText(Path.Combine(root, "platform-tools", "adb.exe"), "placeholder");
        try
        {
            var runner = new ExistingDeviceRunner();
            var controller = new AndroidVmController(AndroidSdkLayout.FromRoot(root), AndroidVmOptions.Default,
                runner, requireFreshStart: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("input") || call.Arguments.Contains("settings"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Product_start_explicitly_binds_the_avd_data_directory()
    {
        var options = AndroidVmOptions.Default with { AvdHome = @"D:\resources\avd" };
        var spec = AndroidCommandFactory.StartEmulator(AndroidSdkLayout.FromRoot(@"D:\resources\sdk"), options);
        Assert.Contains("-datadir", spec.Arguments);
        Assert.Contains(Path.Combine(options.AvdHome, options.AvdName + ".avd"), spec.Arguments);
    }

    private sealed class ExistingDeviceRunner : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            Calls.Add(spec);
            var output = spec.Arguments.Contains("get-state") ? "device" : "rooted_android_game_vm_api35";
            return Task.FromResult(new ProcessResult(0, output, ""));
        }
    }
}
