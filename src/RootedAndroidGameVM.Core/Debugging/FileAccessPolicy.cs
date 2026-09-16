namespace RootedAndroidGameVM.Core.Debugging;

public static class FileAccessPolicy
{
    // Android emulated external storage uses the app's external-data group. A shell-owned
    // 0600 file is invisible to the app even when ADB can hash it. Keep writes scoped.
    public static string FileMode(string scope, string? previousMode = null)
    {
        var prior = previousMode is null ? 0 : Convert.ToInt32(previousMode, 8);
        return scope switch
        {
            "private" => Convert.ToString((prior & Convert.ToInt32("660", 8)) | Convert.ToInt32("600", 8), 8),
            "external" => "660",
            "shared" => Convert.ToString((prior & Convert.ToInt32("664", 8)) | Convert.ToInt32("644", 8), 8),
            _ => throw new ArgumentException("未知文件作用域。")
        };
    }
    public static string DirectoryMode(string scope) => scope switch
    { "private" => "700", "external" => "770", "shared" => "775", _ => throw new ArgumentException("未知文件作用域。") };
}
