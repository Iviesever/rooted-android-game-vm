using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Android;

public sealed record AndroidSdkLayout(string Root, string AdbPath, string EmulatorPath)
{
    public static AndroidSdkLayout FromRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var normalizedRoot = Path.GetFullPath(root);
        return new(
            normalizedRoot,
            Path.Combine(normalizedRoot, "platform-tools", "adb.exe"),
            Path.Combine(normalizedRoot, "emulator", "emulator.exe"));
    }

    public static AndroidSdkLayout Discover(InstallPaths? paths = null) =>
        FromRoot((paths ?? InstallPaths.CreateDefault()).SdkRoot);

    public bool HasRequiredTools => File.Exists(AdbPath) && File.Exists(EmulatorPath);
}
