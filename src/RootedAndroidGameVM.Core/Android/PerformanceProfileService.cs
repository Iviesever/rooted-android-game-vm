using RootedAndroidGameVM.Core.Setup;

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
    private readonly string _productRoot = productRoot ?? InstallPaths.CreateDefault().ProductRoot;

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

    public static PerformanceProfile ReadCurrent(string? productRoot = null)
    {
        var root = productRoot ?? InstallPaths.CreateDefault().ProductRoot;
        var config = Path.Combine(InstallPaths.FromProductRoot(root).AvdHome, "rooted_android_game_vm_api35.avd", "config.ini");
        // The AVD config travels with cold checkpoints. Prefer its restored GPU setting over a stale UI preference file.
        if (File.Exists(config))
        {
            var mode = File.ReadLines(config).FirstOrDefault(line => line.StartsWith("hw.gpu.mode=", StringComparison.Ordinal))?.Split('=', 2)[1].Trim();
            if (mode == "host") return PerformanceProfile.HighPerformance;
            if (mode == "swiftshader_indirect") return PerformanceProfile.Stable;
        }
        var path = Path.Combine(
            root,
            "performance-profile.txt");
        return File.Exists(path) &&
               Enum.TryParse<PerformanceProfile>(File.ReadAllText(path).Trim(), out var profile)
            ? profile
            : PerformanceProfile.Stable;
    }
}
