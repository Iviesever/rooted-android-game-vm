using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class StorageLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rgvm-storage-tests", Guid.NewGuid().ToString("N"));
    private string ControlRoot => Path.Combine(_root, "control");

    private ProductStorageLocation CreateStore() => new(ControlRoot);

    [Fact]
    public void Missing_location_preserves_the_legacy_product_directory()
    {
        Assert.Equal(ControlRoot, CreateStore().ReadRoot());
    }

    [Fact]
    public async Task Selected_location_survives_a_new_store_instance()
    {
        var destination = Path.Combine(_root, "D 盘资源");
        await CreateStore().SaveRootAsync(destination, CancellationToken.None);
        Assert.Equal(destination, CreateStore().ReadRoot());
        Assert.True(File.Exists(Path.Combine(ControlRoot, "storage-location.json")));
    }

    [Fact]
    public void Corrupt_location_fails_instead_of_silently_using_an_empty_default()
    {
        Directory.CreateDirectory(ControlRoot);
        File.WriteAllText(Path.Combine(ControlRoot, "storage-location.json"), "invalid-json");
        var store = CreateStore();
        Assert.Throws<InvalidDataException>(() => { store.ReadRoot(); });
    }

    [Fact]
    public async Task Saving_a_drive_root_or_relative_path_cannot_redirect_the_product()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.SaveRootAsync("relative", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.SaveRootAsync(Path.GetPathRoot(_root)!, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(ControlRoot, "storage-location.json")));
    }

    [Fact]
    public async Task Install_paths_resolve_the_saved_directory()
    {
        var destination = Path.Combine(_root, "data");
        await CreateStore().SaveRootAsync(destination, CancellationToken.None);
        var paths = InstallPaths.CreateDefault(ControlRoot);
        Assert.Equal(destination, paths.ProductRoot);
        Assert.Equal(Path.Combine(destination, "runtime", "avd"), paths.AvdHome);
        Assert.Equal(Path.Combine(destination, "downloads"), paths.DownloadCache);
    }

    [Fact]
    public void Explicit_install_paths_keep_sdk_avd_and_profile_together()
    {
        var paths = InstallPaths.FromProductRoot(Path.Combine(_root, "selected"));
        Directory.CreateDirectory(paths.ProductRoot);
        File.WriteAllText(Path.Combine(paths.ProductRoot, "performance-profile.txt"), "HighPerformance");
        var options = AndroidVmOptions.ForPaths(paths);
        Assert.Equal(paths.AvdHome, options.AvdHome);
        Assert.Equal("host", options.GpuMode);
        Assert.Equal(paths.SdkRoot, AndroidSdkLayout.Discover(paths).Root);
    }

    [Fact]
    public async Task Cancelled_location_change_keeps_the_previous_location()
    {
        var original = Path.Combine(_root, "original");
        var store = CreateStore();
        await store.SaveRootAsync(original);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveRootAsync(Path.Combine(_root, "cancelled"), new CancellationToken(true)));
        Assert.Equal(original, store.ReadRoot());
        Assert.False(Directory.Exists(Path.Combine(_root, "cancelled")));
    }

    [Fact]
    public async Task Missing_selected_drive_or_folder_does_not_fall_back()
    {
        var destination = Path.Combine(_root, "removed");
        var store = CreateStore();
        await store.SaveRootAsync(destination);
        Directory.Delete(destination);
        Assert.Throws<DirectoryNotFoundException>(() => store.ReadRoot());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
