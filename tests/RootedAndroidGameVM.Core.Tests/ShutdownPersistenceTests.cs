using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ShutdownPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_flushes_guest_data_and_does_not_kill_when_flush_fails(bool failSync)
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-shutdown-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "emulator"));
        Directory.CreateDirectory(Path.Combine(root, "platform-tools"));
        File.WriteAllText(Path.Combine(root, "emulator", "emulator.exe"), "placeholder");
        File.WriteAllText(Path.Combine(root, "platform-tools", "adb.exe"), "placeholder");
        try
        {
            var runner = new ShutdownRunner(failSync);
            var controller = new AndroidVmController(AndroidSdkLayout.FromRoot(root), AndroidVmOptions.Default, runner);
            if (failSync)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StopAsync());
                Assert.DoesNotContain("kill", runner.Calls);
            }
            else
            {
                await controller.StopAsync();
                Assert.Contains("sync", runner.Calls);
                Assert.True(runner.Calls.IndexOf("sync") < runner.Calls.IndexOf("kill"));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ShutdownRunner(bool failSync) : IProcessRunner
    {
        private bool _running = true;
        public List<string> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            var command = spec.Arguments.Last();
            Calls.Add(command);
            if (command == "sync") return Task.FromResult(new ProcessResult(failSync ? 1 : 0, "", failSync ? "sync failed" : ""));
            if (command == "kill") _running = false;
            var output = command == "get-state" ? (_running ? "device" : "offline") : "rooted_android_game_vm_api35";
            return Task.FromResult(new ProcessResult(0, output, ""));
        }
    }
}
