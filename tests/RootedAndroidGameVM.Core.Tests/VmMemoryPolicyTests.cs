using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class VmMemoryPolicyTests
{
    [Fact]
    public void Available_headroom_not_total_ram_controls_admission()
    {
        var host = new HostMemorySnapshot(16111, 7000, 32, 57, 16000);
        Assert.True(VmMemoryPolicy.Assess(3072, host).Allowed);
        Assert.False(VmMemoryPolicy.Assess(4096, host).Allowed);
        Assert.False(VmMemoryPolicy.Assess(3072, host with { AvailableMb = 6000 }).Allowed);
        Assert.True(VmMemoryPolicy.Assess(4096, host with { AvailableMb = 10000 }).Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4000L)]
    public void Missing_or_insufficient_commit_rejects_otherwise_free_physical_memory(long? commit)
    {
        Assert.False(VmMemoryPolicy.Assess(3072, new(16111, 10000, 32, 38, commit)).Allowed);
    }

    [Fact]
    public void Pressure_requires_sustained_low_samples_and_resets_on_recovery()
    {
        var tracker = new MemoryPressureTracker();
        var low = new HostMemorySnapshot(16111, 1200, 32, 93, 10000);
        Assert.False(tracker.ShouldStop(low, TimeSpan.Zero));
        Assert.False(tracker.ShouldStop(low, TimeSpan.FromSeconds(5)));
        Assert.False(tracker.ShouldStop(low with { AvailableMb = 3000 }, TimeSpan.FromSeconds(6)));
        Assert.False(tracker.ShouldStop(low, TimeSpan.FromSeconds(7)));
        Assert.False(tracker.ShouldStop(low, TimeSpan.FromSeconds(12)));
        Assert.True(tracker.ShouldStop(low, TimeSpan.FromSeconds(13)));
        tracker.Reset();
        Assert.False(tracker.ShouldStop(low, TimeSpan.FromSeconds(14)));
    }

    [Fact]
    public void Critical_physical_or_commit_pressure_does_not_wait_six_seconds()
    {
        Assert.True(new MemoryPressureTracker().ShouldStop(new(16111, 400, 32, 98, 10000), TimeSpan.Zero));
        Assert.True(new MemoryPressureTracker().ShouldStop(new(16111, 5000, 32, 69, 200), TimeSpan.Zero));
    }

    [Fact]
    public async Task Controller_refuses_before_process_launch_or_any_guest_write()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "emulator"));
        Directory.CreateDirectory(Path.Combine(root, "platform-tools"));
        File.WriteAllText(Path.Combine(root, "emulator", "emulator.exe"), "must never execute");
        File.WriteAllText(Path.Combine(root, "platform-tools", "adb.exe"), "must never execute");
        try
        {
            var runner = new StoppedRunner();
            var options = AndroidVmOptions.Default with { MemoryMb = 4096 };
            var controller = new AndroidVmController(AndroidSdkLayout.FromRoot(root), options, runner,
                readHostMemory: () => new(16111, 6000, 32, 63, 16000));
            var error = await Assert.ThrowsAsync<HostMemoryInsufficientException>(() => controller.StartAsync());
            Assert.Equal("host_memory_low", DebugReply.Failure(error).Error!.Code);
            Assert.Equal(2, runner.Calls.Count);
            Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("shell") || call.Arguments.Contains("wait-for-device"));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class StoppedRunner : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            Calls.Add(spec);
            return Task.FromResult(spec.Arguments.Contains("get-state")
                ? new ProcessResult(1, "offline", "") : new ProcessResult(0, "rooted_android_game_vm_api35", ""));
        }
    }
}
