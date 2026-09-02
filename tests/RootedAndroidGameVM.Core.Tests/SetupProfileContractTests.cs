using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class SetupProfileContractTests
{
    [Fact]
    public void Dependency_profile_pins_https_sources_and_sha256_digests()
    {
        foreach (var dependency in InstallProfile.Dependencies)
        {
            Assert.Equal(Uri.UriSchemeHttps, dependency.Source.Scheme);
            Assert.Equal(64, dependency.Sha256.Length);
            Assert.True(dependency.Sha256.All(Uri.IsHexDigit));
        }
    }

    [Fact]
    public void Android_profile_is_fixed_to_tested_api_35_play_store_x64_image()
    {
        Assert.Equal("system-images;android-35;google_apis_playstore;x86_64", InstallProfile.SystemImagePackage);
        Assert.Contains("platform-tools", InstallProfile.SdkPackages);
        Assert.Contains("emulator", InstallProfile.SdkPackages);
    }

    [Fact]
    public void Install_paths_stay_below_a_single_per_user_product_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-install-root");
        var paths = InstallPaths.FromProductRoot(root);

        Assert.StartsWith(Path.GetFullPath(root), paths.SdkRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root), paths.JavaHome, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root), paths.DownloadCache, StringComparison.OrdinalIgnoreCase);
    }
}
