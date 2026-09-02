namespace RootedAndroidGameVM.Core.Android;

public enum PerformanceProfile
{
    Stable,
    HighPerformance
}

public sealed class PerformanceProfileService(
    AndroidVmOptions options,
    string? productRoot = null)
{
    private readonly string _productRoot = productRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RootedAndroidGameVM");

    public async Task ApplyAsync(
        PerformanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var avdHome = options.AvdHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".android",
            "avd");
        var config = Path.Combine(avdHome, $"{options.AvdName}.avd", "config.ini");
        await AvdConfigEditor.UpsertAsync(
            config,
            new Dictionary<string, string>
            {
                ["hw.gpu.enabled"] = "yes",
                ["hw.gpu.mode"] = profile == PerformanceProfile.HighPerformance
                    ? "host"
                    : "swiftshader_indirect"
            },
            cancellationToken);

        Directory.CreateDirectory(_productRoot);
        await File.WriteAllTextAsync(
            Path.Combine(_productRoot, "performance-profile.txt"),
            profile.ToString(),
            cancellationToken);
    }

    public static PerformanceProfile ReadCurrent()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RootedAndroidGameVM",
            "performance-profile.txt");
        return File.Exists(path) &&
               Enum.TryParse<PerformanceProfile>(File.ReadAllText(path).Trim(), out var profile)
            ? profile
            : PerformanceProfile.Stable;
    }
}
