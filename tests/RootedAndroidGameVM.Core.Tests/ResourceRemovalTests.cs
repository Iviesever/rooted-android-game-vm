using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ResourceRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rgvm-removal-tests", Guid.NewGuid().ToString("N"));
    private string DataRoot => Path.Combine(_root, "selected-data");
    private ProductStorageLocation Location => new(Path.Combine(_root, "control"));

    private async Task SeedAsync()
    {
        await StorageOwnership.InitializeAsync(DataRoot, Location);
        Directory.CreateDirectory(Path.Combine(DataRoot, "runtime", "avd"));
        Directory.CreateDirectory(Path.Combine(DataRoot, "downloads"));
        File.WriteAllText(Path.Combine(DataRoot, "runtime", "avd", "userdata.img"), "dummy data");
        File.WriteAllText(Path.Combine(DataRoot, "downloads", "cached.zip"), "cache");
        File.WriteAllText(Path.Combine(_root, "personal.txt"), "leave alone");
    }

    private dynamic Service()
    {
        var type = typeof(InstallPaths).Assembly.GetType("RootedAndroidGameVM.Core.Storage.ResourceRemovalService");
        Assert.NotNull(type);
        return Activator.CreateInstance(type, [Location, (Func<InstallPaths, CancellationToken, Task>)((_, _) => Task.CompletedTask)])!;
    }

    [Fact]
    public async Task Runtime_removal_uses_selected_root_and_preserves_cache_and_location()
    {
        await SeedAsync();
        await Service().RemoveAsync("runtime", CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(DataRoot, "runtime")));
        Assert.Equal("cache", File.ReadAllText(Path.Combine(DataRoot, "downloads", "cached.zip")));
        Assert.Equal(DataRoot, Location.ReadRoot());
        Assert.True(StorageOwnership.IsOwned(DataRoot));
        Assert.Equal("leave alone", File.ReadAllText(Path.Combine(_root, "personal.txt")));
    }

    [Fact]
    public async Task All_resources_removal_does_not_touch_neighboring_files()
    {
        await SeedAsync();
        await Service().RemoveAsync("all", CancellationToken.None);
        Assert.False(Directory.Exists(DataRoot));
        Assert.False(File.Exists(Location.LocationFilePath));
        Assert.Equal("leave alone", File.ReadAllText(Path.Combine(_root, "personal.txt")));
    }

    [Fact]
    public async Task Unrecognized_directory_is_never_removed()
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(Path.Combine(DataRoot, "keep.txt"), "unrelated");
        await Location.SaveRootAsync(DataRoot);
        var service = Service();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.RemoveAsync("all", CancellationToken.None));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(DataRoot, "keep.txt")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
