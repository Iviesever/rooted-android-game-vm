namespace RootedAndroidGameVM.Core.Android;

public sealed record AndroidVmOptions(
    string AvdName,
    string Serial,
    int Port,
    string GpuMode,
    int MemoryMb,
    string? AvdHome = null)
{
    public static AndroidVmOptions ProductDefault
    {
        get
        {
            var productAvdHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RootedAndroidGameVM",
                "runtime",
                "avd");
            var profile = PerformanceProfileService.ReadCurrent();
            return new(
                "rooted_android_game_vm_api35",
                "emulator-5554",
                5554,
                profile == PerformanceProfile.HighPerformance ? "host" : "swiftshader_indirect",
                4096,
                productAvdHome);
        }
    }

    public static AndroidVmOptions Default => ProductDefault;
}
