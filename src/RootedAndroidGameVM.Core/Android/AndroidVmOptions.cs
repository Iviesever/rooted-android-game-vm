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
    bool Verbose = false,
    int? GrpcPort = null,
    int CpuCores = 4,
    bool Vulkan = false,
    int StartAvailableMb = 0)
{
    public static AndroidVmOptions ForPaths(InstallPaths paths)
    {
        var profile = new RuntimeProfileStore(paths).Read();
        return new(
            "rooted_android_game_vm_api35", "emulator-5554", 5554,
            profile.Renderer, profile.MemoryMb, paths.AvdHome, CpuCores: profile.CpuCores, Vulkan: profile.Vulkan, StartAvailableMb: profile.StartAvailableMb);
    }

    public static AndroidVmOptions ProductDefault => ForPaths(InstallPaths.CreateDefault());

    public static AndroidVmOptions Default => ProductDefault;
}
