using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Android;

public static class AndroidCommandFactory
{
    public static ProcessRequest ListAvds(
        AndroidSdkLayout layout,
        AndroidVmOptions options) =>
        new(
            new ProcessSpec(
                layout.EmulatorPath,
                ["-list-avds"],
                Path.GetDirectoryName(layout.EmulatorPath)),
            EnvironmentVariables: string.IsNullOrWhiteSpace(options.AvdHome)
                ? null
                : new Dictionary<string, string>
                {
                    ["ANDROID_AVD_HOME"] = options.AvdHome
                });

    public static ProcessSpec StartEmulator(AndroidSdkLayout layout, AndroidVmOptions options)
    {
        var arguments = new List<string>
        {
            "-avd", options.AvdName,
            "-port", options.Port.ToString(),
            "-gpu", options.GpuMode,
            "-feature", options.Vulkan ? "Vulkan" : "-Vulkan",
            "-memory", options.MemoryMb.ToString(),
            "-cores", options.CpuCores.ToString(),
            "-no-snapshot-load", "-no-snapshot-save"
        };
        if (!string.IsNullOrWhiteSpace(options.AvdHome))
            arguments.AddRange(["-datadir", Path.Combine(options.AvdHome, options.AvdName + ".avd")]);
        if (options.Headless)
        {
            arguments.AddRange(["-no-window", "-no-audio", "-no-boot-anim"]);
        }
        if (options.LowRam) arguments.Add("-lowram");
        if (options.Verbose)
        {
            arguments.Add("-verbose");
        }
        if (options.GrpcPort is int grpcPort)
            arguments.AddRange(["-grpc", grpcPort.ToString(), "-grpc-use-token"]);
        return new(
            layout.EmulatorPath,
            arguments,
            Path.GetDirectoryName(layout.EmulatorPath));
    }

    public static ProcessSpec Adb(AndroidSdkLayout layout, AndroidVmOptions options, params string[] arguments) =>
        new(layout.AdbPath, ["-s", options.Serial, .. arguments], layout.Root);

    public static ProcessSpec InstallApk(
        AndroidSdkLayout layout,
        AndroidVmOptions options,
        string apkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        return Adb(layout, options, "install", "-r", apkPath);
    }

    public static ProcessSpec LaunchPackage(
        AndroidSdkLayout layout,
        AndroidVmOptions options,
        AndroidPackageName packageName) =>
        Adb(layout, options, "shell", "monkey", "-p", packageName.Value,
            "-c", "android.intent.category.LAUNCHER", "1");

    public static ProcessSpec StopEmulator(AndroidSdkLayout layout, AndroidVmOptions options) =>
        Adb(layout, options, "emu", "kill");

    public static ProcessSpec ForceStopPackage(
        AndroidSdkLayout layout,
        AndroidVmOptions options,
        AndroidPackageName packageName) =>
        Adb(layout, options, "shell", "am", "force-stop", packageName.Value);

    public static ProcessSpec UninstallPackage(
        AndroidSdkLayout layout,
        AndroidVmOptions options,
        AndroidPackageName packageName) =>
        Adb(layout, options, "uninstall", packageName.Value);

    public static ProcessSpec RootIdentity(AndroidSdkLayout layout, AndroidVmOptions options) =>
        RootShell(layout, options, "id");

    // Magisk's early-boot mount is not necessarily on Android's default shell PATH.
    // Apply the path in both shells because su may reset the environment.
    private const string RootPath = "export PATH=/debug_ramdisk:/sbin:$PATH; ";

    public static ProcessSpec RootShell(AndroidSdkLayout layout, AndroidVmOptions options, string script) =>
        Adb(layout, options, "shell", RootPath + "exec su -c " + QuoteShell(RootPath + script));

    public static ProcessSpec RootExecOut(AndroidSdkLayout layout, AndroidVmOptions options, string script) =>
        Adb(layout, options, "exec-out", RootPath + "exec su -c " + QuoteShell(RootPath + script));

    public static ProcessSpec FindRootShell(AndroidSdkLayout layout, AndroidVmOptions options) =>
        Adb(layout, options, "shell", RootPath + "command -v su");

    private static string QuoteShell(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public static ProcessSpec WakeDevice(AndroidSdkLayout layout, AndroidVmOptions options) =>
        Adb(layout, options, "shell", "input", "keyevent", "KEYCODE_WAKEUP");

    public static ProcessSpec DismissKeyguard(AndroidSdkLayout layout, AndroidVmOptions options) =>
        Adb(layout, options, "shell", "wm", "dismiss-keyguard");

    public static ProcessSpec CheckPackageService(AndroidSdkLayout layout, AndroidVmOptions options) =>
        Adb(layout, options, "shell", "service", "check", "package");
}
