using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class SetupGuiTestOptionsTests
{
    [Fact]
    public void Ordinary_start_has_no_test_override() => Assert.Null(SetupGuiTestOptions.TryParse([], _ => null));

    [Fact]
    public void Gui_test_override_requires_explicit_gate() => Assert.Throws<ArgumentException>(() =>
        SetupGuiTestOptions.TryParse(["--e2e-gui", "--control-root", @"D:\isolated", "--port", "5564"], _ => null));

    [Fact]
    public void Gui_test_override_is_scoped_to_a_named_root_and_even_port()
    {
        var options = SetupGuiTestOptions.TryParse(["--e2e-gui", "--control-root", @"D:\isolated", "--port", "5564"], _ => "1");
        Assert.Equal(@"D:\isolated", options!.ControlRoot);
        Assert.Equal(5564, options.Port);
        Assert.Throws<ArgumentException>(() =>
            SetupGuiTestOptions.TryParse(["--e2e-gui", "--control-root", @"D:\", "--port", "5564"], _ => "1"));
        Assert.Throws<ArgumentException>(() =>
            SetupGuiTestOptions.TryParse(["--e2e-gui", "--control-root", @"D:\isolated", "--port", "5565"], _ => "1"));
    }
}
