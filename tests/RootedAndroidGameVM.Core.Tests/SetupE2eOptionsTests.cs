using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class SetupE2eOptionsTests
{
    [Fact]
    public void Normal_gui_launch_does_not_enter_e2e_mode()
    {
        Assert.Null(SetupE2eOptions.TryParse([], _ => null));
    }

    [Fact]
    public void E2e_mode_requires_an_explicit_sdk_license_environment_gate()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SetupE2eOptions.TryParse(
                ["--e2e", "--product-root", @"D:\safe", "--avd-name", "rgvm_e2e", "--port", "5574"],
                _ => null));
    }

    [Fact]
    public void E2e_mode_parses_an_isolated_root_and_even_emulator_port()
    {
        var options = SetupE2eOptions.TryParse(
            ["--e2e", "--product-root", @"D:\safe", "--avd-name", "rgvm_e2e", "--port", "5574"],
            name => name == "RGVM_E2E_ACCEPT_SDK_LICENSE" ? "1" : null);

        Assert.NotNull(options);
        Assert.Equal(Path.GetFullPath(@"D:\safe"), options.ProductRoot);
        Assert.Equal("rgvm_e2e", options.AvdName);
        Assert.Equal(5574, options.Port);
        Assert.Equal("emulator-5574", options.Serial);
    }
}
