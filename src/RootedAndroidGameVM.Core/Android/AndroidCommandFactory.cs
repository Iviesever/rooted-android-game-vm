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
            "-feature", "-Vulkan",
            "-memory", options.MemoryMb.ToString(),
            "-no-snapshot-load"
        };
        if (options.Headless)
        {
            arguments.AddRange(["-no-window", "-no-audio", "-no-boot-anim"]);
        }
        if (options.Verbose)
        {
            arguments.Add("-verbose");
        }
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
        Adb(layout, options, "shell", "su", "-c", "id");

    public static ProcessSpec WakeDevice(AndroidSdkLayout layout, AndroidVmOptions options) =>
        Adb(layout, options, "shell", "input", "keyevent", "KEYCODE_WAKEUP");

    public static ProcessSpec DismissKeyguard(AndroidSdkLayout layout, AndroidVmOptions options) =>
        Adb(layout, options, "shell", "wm", "dismiss-keyguard");
}
