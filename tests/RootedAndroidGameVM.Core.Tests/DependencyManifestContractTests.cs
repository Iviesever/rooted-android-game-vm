using RootedAndroidGameVM.Core.Dependencies;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class DependencyManifestContractTests
{
    [Fact]
    public void Embedded_manifest_is_the_single_source_for_pinned_sdk_archives()
    {
        var manifest = DependencyManifest.LoadEmbedded();

        var platformTools = Assert.Single(manifest.Components, item => item.Id == "android-platform-tools");
        Assert.Equal("37.0.1", platformTools.Version);
        Assert.Equal("platform-tools_r37.0.1-win.zip", platformTools.ArchiveFileName);
        Assert.Equal("e03e78b1d80b396f1c3358e31251cb31740e1110", platformTools.UpstreamSha1);

        var emulator = Assert.Single(manifest.Components, item => item.Id == "android-emulator");
        Assert.Equal("37.1.11", emulator.Version);
        Assert.Equal("emulator-windows_x64-15917651.zip", emulator.ArchiveFileName);
        Assert.Equal("54fa750822ff462d57e04fc8e98e60f08df2bb61", emulator.UpstreamSha1);

        var image = Assert.Single(manifest.Components, item => item.Id == "android-system-image-api35-playstore-x86_64");
        Assert.Equal("9", image.Version);
        Assert.Equal("x86_64-35_r09.zip", image.ArchiveFileName);
        Assert.Equal("2f0054868e6aab3c098acd3decba17a82aed4176", image.UpstreamSha1);
    }

    [Fact]
    public void Every_downloadable_component_has_an_immutable_https_url_and_checksum()
    {
        var manifest = DependencyManifest.LoadEmbedded();

        foreach (var component in manifest.Components.Where(item => item.DownloadAtInstall))
        {
            Assert.True(Uri.TryCreate(component.Url, UriKind.Absolute, out var uri));
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
            Assert.Equal(64, component.Sha256.Length);
            Assert.True(component.Sha256.All(Uri.IsHexDigit));
        }
    }

    [Fact]
    public void Published_wpf_projects_pin_the_manifest_runtime_patch()
    {
        var runtimeVersion = DependencyManifest.LoadEmbedded().Required("dotnet-runtime").Version;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        foreach (var path in new[]
        {
            Path.Combine(root, "src", "RootedAndroidGameVM.Launcher", "RootedAndroidGameVM.Launcher.csproj"),
            Path.Combine(root, "src", "RootedAndroidGameVM.Setup", "RootedAndroidGameVM.Setup.csproj")
        })
        {
            var project = System.Xml.Linq.XDocument.Load(path);
            Assert.Equal(runtimeVersion, project.Descendants("RuntimeFrameworkVersion").Single().Value);
        }
    }

    [Fact]
    public void Inno_setup_build_tool_uses_the_immutable_official_release_asset()
    {
        var inno = DependencyManifest.LoadEmbedded().Required("inno-setup");

        Assert.Equal("6.7.3", inno.Version);
        Assert.Equal("innosetup-6.7.3.exe", inno.ArchiveFileName);
        Assert.StartsWith(
            "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/",
            inno.Url,
            StringComparison.Ordinal);
        Assert.Equal(64, inno.Sha256.Length);
        Assert.True(inno.Sha256.All(Uri.IsHexDigit));
        Assert.Equal(10592232, inno.Size);
    }
}
