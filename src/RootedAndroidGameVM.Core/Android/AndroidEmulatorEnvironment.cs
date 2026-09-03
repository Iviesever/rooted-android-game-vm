namespace RootedAndroidGameVM.Core.Android;

public static class AndroidEmulatorEnvironment
{
    public static IReadOnlyDictionary<string, string> Create(
        AndroidSdkLayout layout,
        AndroidVmOptions options)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(options);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANDROID_HOME"] = layout.Root,
            ["ANDROID_SDK_ROOT"] = layout.Root
        };
        if (!string.IsNullOrWhiteSpace(options.AvdHome))
        {
            environment["ANDROID_AVD_HOME"] = options.AvdHome;
        }
        return environment;
    }
}
