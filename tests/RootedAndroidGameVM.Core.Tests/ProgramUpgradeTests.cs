using System.Reflection;
using System.Text.Json;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ProgramUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rgvm-upgrade-tests", Guid.NewGuid().ToString("N"));
    private InstallPaths Paths => InstallPaths.FromProductRoot(_root);
    private string Ramdisk => Path.Combine(Paths.SdkRoot, InstallProfile.SdkComponents.Single(component =>
        component.PackagePath == InstallProfile.SystemImagePackage).RelativeDirectory, "ramdisk.img");

    private Task<bool> ProbeAsync()
    {
        var type = typeof(InstallPaths).Assembly.GetType("RootedAndroidGameVM.Core.Setup.ProgramUpgradeProbe");
        Assert.NotNull(type);
        var method = type.GetMethod("CanReuseAsync", BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task<bool>>(method.Invoke(null, [Paths, CancellationToken.None]));
    }

    private async Task SeedAsync()
    {
        Directory.CreateDirectory(Path.Combine(Paths.AvdHome, "rooted_android_game_vm_api35.avd"));
        File.WriteAllText(Path.Combine(Paths.AvdHome, "rooted_android_game_vm_api35.avd", "config.ini"), "hw.keyboard=yes");
        foreach (var component in InstallProfile.SdkComponents)
        {
            var directory = Path.Combine(Paths.SdkRoot, component.RelativeDirectory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "source.properties"), "Pkg.Revision=" + component.Revision);
        }
        File.WriteAllText(Path.Combine(Paths.SdkRoot, "platform-tools", "adb.exe"), "test tool");
        File.WriteAllText(Path.Combine(Paths.SdkRoot, "emulator", "emulator.exe"), "test tool");
        File.WriteAllText(Ramdisk, "verified patched ramdisk");
        File.WriteAllText(Path.Combine(Paths.RuntimeRoot, "userdata-preserve.bin"), "existing application data");
        File.WriteAllText(Path.Combine(_root, "install.json"), JsonSerializer.Serialize(new
        {
            version = "0.1.2", sdkRoot = Paths.SdkRoot, avdHome = Paths.AvdHome, avdName = "rooted_android_game_vm_api35"
        }));
        await new InstallJournalStore(Path.Combine(_root, "install-state.json")).UpdateAsync(SetupStage.Complete,
            Paths.SdkRoot, Paths.AvdHome, "rooted_android_game_vm_api35", new string('a', 64), await Sha256Verifier.ComputeAsync(Ramdisk));
    }

    [Fact]
    public async Task Compatible_completed_runtime_is_reused_without_recreating_or_writing_data()
    {
        await SeedAsync();
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => File.GetLastWriteTimeUtc(path));
        Assert.True(await ProbeAsync());
        Assert.Equal("existing application data", File.ReadAllText(Path.Combine(Paths.RuntimeRoot, "userdata-preserve.bin")));
        Assert.All(before, item => Assert.Equal(item.Value, File.GetLastWriteTimeUtc(item.Key)));
    }

    [Fact]
    public async Task Changed_ramdisk_requires_configuration_instead_of_a_false_upgrade_success()
    {
        await SeedAsync();
        File.WriteAllText(Ramdisk, "changed ramdisk");
        Assert.False(await ProbeAsync());
    }

    [Fact]
    public async Task Incomplete_installation_is_not_treated_as_a_completed_runtime()
    {
        await SeedAsync();
        await new InstallJournalStore(Path.Combine(_root, "install-state.json")).UpdateAsync(SetupStage.Root,
            Paths.SdkRoot, Paths.AvdHome, "rooted_android_game_vm_api35");
        Assert.False(await ProbeAsync());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
