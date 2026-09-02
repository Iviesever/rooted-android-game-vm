namespace RootedAndroidGameVM.Core.Android;

public sealed record AndroidVmOptions(
    string AvdName,
    string Serial,
    int Port,
    string GpuMode,
    int MemoryMb,
    string? AvdHome = null)
{
    public static AndroidVmOptions Default
    {
        get
        {
            var productAvdHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RootedAndroidGameVM",
                "runtime",
                "avd");
            var installedProductAvd = Path.Combine(productAvdHome, "arcaea_root_api35.avd");
            var profile = PerformanceProfileService.ReadCurrent();
            return new(
                "arcaea_root_api35",
                "emulator-5554",
                5554,
                profile == PerformanceProfile.HighPerformance ? "host" : "swiftshader_indirect",
                4096,
                Directory.Exists(installedProductAvd) ? productAvdHome : null);
        }
    }
}
