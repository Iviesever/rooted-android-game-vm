using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class LowRamProfileTests
{
    [Fact]
    public void Low_ram_is_explicit_and_survives_config_to_launch_arguments()
    {
        var profile = RuntimeProfile.Recommended with { MemoryMb = 1024, LowRam = true, VmHeapMb = 576 };
        profile.Validate(16111, 32);
        Assert.Throws<ArgumentException>(() => (profile with { LowRam = false }).Validate());
        Assert.Throws<ArgumentException>(() => (profile with { MemoryMb = 767 }).Validate());
        var recovered = RuntimeProfile.FromAvdSettings(profile.ToAvdSettings().Select(pair => pair.Key + "=" + pair.Value));
        Assert.Equal(profile, recovered);
        Assert.False(RuntimeProfile.FromAvdSettings([]).LowRam);
        var options = new AndroidVmOptions("test", "emulator-5554", 5554, "host", 1024, LowRam: true);
        var layout = AndroidSdkLayout.FromRoot(Path.GetTempPath());
        var command = AndroidCommandFactory.StartEmulator(layout, options);
        Assert.Contains("-lowram", command.Arguments);
        Assert.DoesNotContain("-lowram", AndroidCommandFactory.StartEmulator(layout, options with { LowRam = false }).Arguments);
        Assert.Contains("1024", command.Arguments);
    }

    [Fact]
    public void Admission_accounts_for_engine_floor_and_keeps_commit_guard()
    {
        var host = new HostMemorySnapshot(16111, 4096, 32, 75, 10000);
        var normal = VmMemoryPolicy.Assess(1536, host, 4096);
        Assert.Equal(2560 + VmMemoryPolicy.ResidentOverheadMb, normal.EstimatedVmMb);
        var low = VmMemoryPolicy.Assess(1024, host, 4096, lowRam: true);
        Assert.Equal(1024 + VmMemoryPolicy.ResidentOverheadMb, low.EstimatedVmMb);
        Assert.True(low.Allowed);
        Assert.False(VmMemoryPolicy.Assess(1024, host with { AvailableCommitMb = 100 }, 4096, true).Allowed);
        Assert.Throws<ArgumentException>(() => VmMemoryPolicy.Assess(1024, host, 4096));
    }
}
