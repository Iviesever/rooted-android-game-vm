using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class GenericApplicationContractTests
{
    [Theory]
    [InlineData("launch")]
    [InlineData("force-stop")]
    [InlineData("uninstall")]
    [InlineData("app.observe")]
    [InlineData("app.page.annotate")]
    [InlineData("logs")]
    [InlineData("metrics")]
    [InlineData("frames.sample")]
    [InlineData("files.list")]
    [InlineData("files.push")]
    [InlineData("files.pull")]
    [InlineData("files.export")]
    public async Task Missing_application_fails_before_using_a_device_or_creating_artifacts(string command)
    {
        var path = Path.Combine(Path.GetTempPath(), "rgvm-no-target-" + Guid.NewGuid().ToString("N"));
        using var service = new AndroidDebugService(InstallPaths.FromProductRoot(path));
        var error = await Assert.ThrowsAsync<DebugException>(() => service.ExecuteAsync(new(command), default));
        Assert.Equal("app_required", error.Code);
        Assert.Equal("resolving_application", error.Stage);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Shared_files_do_not_require_an_arbitrary_application_identity()
    {
        Assert.Equal("/sdcard/Download/a.txt", AndroidDebugService.RemotePath("shared", "", "a.txt"));
        Assert.Throws<ArgumentException>(() => AndroidDebugService.RemotePath("shared", "", "../a.txt"));
        Assert.Throws<ArgumentException>(() => AndroidDebugService.RequirePackage(DebugRequest.Create("launch", new { package = "notes;id" })));
    }

    [Theory]
    [InlineData("Document list")]
    [InlineData("设置 / 显示")]
    public void Observation_labels_are_application_independent(string label) => AndroidDebugService.ValidatePageLabel(label);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("page\nsecond")]
    public void Invalid_observation_labels_are_rejected(string label) =>
        Assert.Throws<ArgumentException>(() => AndroidDebugService.ValidatePageLabel(label));

    [Fact]
    public async Task Dedicated_commands_are_not_advertised_or_executable_in_the_core()
    {
        Assert.DoesNotContain(DebugCommandCatalog.Commands, command => command.Name.StartsWith("malody.", StringComparison.Ordinal));
        var path = Path.Combine(Path.GetTempPath(), "rgvm-no-plugin-" + Guid.NewGuid().ToString("N"));
        using var service = new AndroidDebugService(InstallPaths.FromProductRoot(path));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(new("malody.import"), default));
        Assert.False(Directory.Exists(path));
    }
}
