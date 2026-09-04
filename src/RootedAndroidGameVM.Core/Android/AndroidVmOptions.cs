using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Android;

public sealed record AndroidVmOptions(
    string AvdName,
    string Serial,
    int Port,
    string GpuMode,
    int MemoryMb,
    string? AvdHome = null,
    bool Headless = false,
    bool Verbose = false)
{
    public static AndroidVmOptions ForPaths(InstallPaths paths)
    {
        var profile = PerformanceProfileService.ReadCurrent(paths.ProductRoot);
        return new(
            "rooted_android_game_vm_api35", "emulator-5554", 5554,
            profile == PerformanceProfile.HighPerformance ? "host" : "swiftshader_indirect",
            4096, paths.AvdHome);
    }

    public static AndroidVmOptions ProductDefault => ForPaths(InstallPaths.CreateDefault());

    public static AndroidVmOptions Default => ProductDefault;
}
