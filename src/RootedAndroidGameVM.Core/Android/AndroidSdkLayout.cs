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

    public static AndroidSdkLayout Discover()
    {
        var configured = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("ANDROID_HOME");
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var productSdk = Path.Combine(localAppData, "RootedAndroidGameVM", "runtime", "android-sdk");
            var productLayout = FromRoot(productSdk);
            if (productLayout.HasRequiredTools)
            {
                return productLayout;
            }

            configured = Path.Combine(localAppData, "Android", "Sdk");
        }

        return FromRoot(configured);
    }

    public bool HasRequiredTools => File.Exists(AdbPath) && File.Exists(EmulatorPath);
}
