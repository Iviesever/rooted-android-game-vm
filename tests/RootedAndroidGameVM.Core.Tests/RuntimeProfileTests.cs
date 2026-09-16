using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class RuntimeProfileTests
{
    [Fact]
    public void Display_name_localization_does_not_hide_refresh_evidence()
    {
        var data = DisplayTelemetry.Parse("DisplayDeviceInfo{\"内置屏幕\": renderFrameRate 120.00001, supportedModes [{fps=120.00001}], FLAG_ALLOWED_TO_BE_DEFAULT_DISPLAY}\nmActiveSfDisplayMode=DisplayMode{width=1920, height=1080, vsyncRate=120.00001}", "");
        Assert.Single(data.SupportedRefreshRates); Assert.Equal(120.00001, data.CompositorRenderRate);
    }
    [Fact]
    public void Hardware_and_120_hz_are_the_recommended_request_not_an_observed_claim()
    {
        var requested = RuntimeProfile.Recommended.Validate(16 * 1024, 32);
        Assert.Equal("host", requested.Renderer);
        Assert.Equal(120, requested.RefreshRate);
        var observed = DisplayTelemetry.Parse("mActiveSfDisplayMode=DisplayMode{id=0, width=1080, height=2400, vsyncRate=60.000004}", "GLES: test");
        Assert.False(observed.ConfirmsRequestedRate(requested.RefreshRate));
        Assert.Equal(60.000004, observed.ActiveRefreshRate);
    }

    [Fact]
    public void Existing_avd_values_survive_a_profile_roundtrip()
    {
        var old = RuntimeProfile.FromAvdSettings(["hw.gpu.mode=host", "hw.lcd.width=1080", "hw.lcd.height=2400", "hw.lcd.vsync=60", "hw.lcd.density=420", "hw.initialOrientation=portrait"]);
        var recovered = RuntimeProfile.FromAvdSettings(old.ToAvdSettings().Select(pair => pair.Key + "=" + pair.Value));
        Assert.Equal(old, recovered);
        Assert.Equal(2400, recovered.Height);
    }

    [Fact]
    public void Unsafe_resource_requests_and_unrecognized_rates_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => (RuntimeProfile.Recommended with { MemoryMb = 8192 }).Validate(8192, 8));
        Assert.Throws<ArgumentException>(() => (RuntimeProfile.Recommended with { CpuCores = 16 }).Validate(16384, 8));
        Assert.Throws<ArgumentException>(() => (RuntimeProfile.Recommended with { RefreshRate = 999 }).Validate());
        Assert.Throws<ArgumentException>(() => (RuntimeProfile.Recommended with { Width = 3840, Height = 3840 }).Validate());
    }

    [Fact]
    public void Missing_display_evidence_is_unavailable_not_zero()
    {
        var display = DisplayTelemetry.Parse("service unavailable", "");
        Assert.Null(display.ActiveRefreshRate);
        Assert.Null(display.Width);
        Assert.Empty(display.SupportedRefreshRates);
        Assert.False(display.ConfirmsRequestedRate(120));
    }
}
