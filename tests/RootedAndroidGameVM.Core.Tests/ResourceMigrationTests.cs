using System.Text.Json;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ResourceMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rgvm-migration-tests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "old");
    private string Target => Path.Combine(_root, "new");
    private string UserData => Path.Combine(Source, "runtime", "userdata.bin");
    private ProductStorageLocation Location => new(Path.Combine(_root, "control"));

    private async Task SeedAsync()
    {
        var paths = InstallPaths.FromProductRoot(Source);
        Directory.CreateDirectory(paths.RuntimeRoot);
        File.WriteAllText(UserData, "dummy private data survives migration");
        File.WriteAllText(Path.Combine(Source, "install.json"), JsonSerializer.Serialize(new
        {
            version = "0.1.2",
            sdkRoot = paths.SdkRoot,
            avdHome = paths.AvdHome,
            avdName = "rooted_android_game_vm_api35"
        }));
        await Location.SaveRootAsync(Source);
    }

    private ResourceMigrationService CreateService(Func<InstallPaths, CancellationToken, Task>? verify = null,
        ProductStorageLocation? location = null) => new(location ?? Location,
        (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, verify ?? ((_, _) => Task.CompletedTask));

    [Fact]
    public async Task Successful_migration_verifies_before_switching_and_reclaims_source()
    {
        await SeedAsync();
        var verified = false;
        var service = CreateService((paths, _) =>
        {
            Assert.Equal(Source, Location.ReadRoot());
            Assert.True(File.Exists(UserData));
            Assert.Equal("dummy private data survives migration", File.ReadAllText(Path.Combine(paths.RuntimeRoot, "userdata.bin")));
            verified = true;
            return Task.CompletedTask;
        });
        await service.MigrateAsync(Target, null, CancellationToken.None);
        Assert.True(verified);
        Assert.Equal(Target, Location.ReadRoot());
        Assert.False(File.Exists(UserData));
        Assert.Equal("dummy private data survives migration", File.ReadAllText(Path.Combine(Target, "runtime", "userdata.bin")));
    }

    [Fact]
    public async Task Failed_start_preserves_original_until_explicit_recovery()
    {
        await SeedAsync();
        var service = CreateService((_, _) => throw new InvalidOperationException("simulated startup failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.MigrateAsync(Target, null, CancellationToken.None));
        Assert.Equal(Source, Location.ReadRoot());
        Assert.True(File.Exists(UserData));
        Assert.True(File.Exists(Path.Combine(Location.ControlRoot, "storage-migration.json")));
        await CreateService().RecoverAsync(null, CancellationToken.None);
        Assert.Equal(Source, Location.ReadRoot());
        Assert.True(File.Exists(UserData));
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task Verification_and_stop_failures_are_both_reported_without_switching_the_source()
    {
        await SeedAsync();
        var service = new ResourceMigrationService(Location,
            (paths, _) => paths.ProductRoot == Source ? Task.CompletedTask : throw new IOException("stop failure"),
            (_, _, _) => Task.CompletedTask,
            (_, _) => throw new InvalidOperationException("verification failure"));
        var error = await Assert.ThrowsAsync<AggregateException>(() => service.MigrateAsync(Target));
        Assert.Contains(error.InnerExceptions, item => item.Message == "verification failure");
        Assert.Contains(error.InnerExceptions, item => item.Message == "stop failure");
        Assert.Equal(Source, Location.ReadRoot());
        Assert.True(File.Exists(UserData));
    }

    [Fact]
    public async Task Existing_target_files_are_never_overwritten()
    {
        await SeedAsync();
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "keep.txt"), "unrelated");
        var service = CreateService();
        await Assert.ThrowsAsync<IOException>(async () => await service.MigrateAsync(Target, null, CancellationToken.None));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(Target, "keep.txt")));
        Assert.Equal(Source, Location.ReadRoot());
    }

    [Fact]
    public async Task Source_changes_during_verification_abort_before_switch()
    {
        await SeedAsync();
        var service = CreateService((_, _) =>
        {
            File.WriteAllText(Path.Combine(Source, "new-user-file.txt"), "do not delete");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(async () => await service.MigrateAsync(Target, null, CancellationToken.None));
        Assert.Equal(Source, Location.ReadRoot());
        Assert.Equal("do not delete", File.ReadAllText(Path.Combine(Source, "new-user-file.txt")));
        Assert.True(File.Exists(UserData));
    }

    [Fact]
    public async Task Recovery_finishes_a_verified_migration_after_locator_write_failure()
    {
        await SeedAsync();
        var service = CreateService();
        using (var lockedLocation = new FileStream(Location.LocationFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(async () => await service.MigrateAsync(Target, null, CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Equal(MigrationStage.Verified, MigrationJournal.Read(Location)!.Stage);
        }
        Assert.True(File.Exists(UserData));
        await CreateService().RecoverAsync(null, CancellationToken.None);
        Assert.Equal(Target, Location.ReadRoot());
        Assert.False(File.Exists(UserData));
        Assert.True(File.Exists(Path.Combine(Target, "runtime", "userdata.bin")));
    }

    [Fact]
    public async Task Cancelled_copy_can_recover_when_the_selected_target_was_an_empty_folder()
    {
        await SeedAsync();
        Directory.CreateDirectory(Target);
        using var cancelled = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.Stage == "正在复制资源") cancelled.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().MigrateAsync(Target, progress, cancelled.Token));
        await CreateService().RecoverAsync();
        Assert.Equal(Source, Location.ReadRoot());
        Assert.True(File.Exists(UserData));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".rgvm-migration-*"));
    }

    [Fact]
    public async Task Moving_the_legacy_control_directory_keeps_only_small_control_files()
    {
        await SeedAsync();
        var legacyLocation = new ProductStorageLocation(Source);
        await legacyLocation.SaveRootAsync(Source);
        await CreateService(location: legacyLocation).MigrateAsync(Target);
        Assert.Equal(Target, legacyLocation.ReadRoot());
        Assert.True(File.Exists(legacyLocation.LocationFilePath));
        Assert.Empty(VerifiedDirectoryCopy.ReadInventory(Source, StorageOwnership.ControlFiles).Files);
    }

    [Fact]
    public async Task Completed_migration_keeps_a_durable_verification_receipt()
    {
        await SeedAsync();
        await CreateService().MigrateAsync(Target);
        var receipt = Path.Combine(Location.ControlRoot, "storage-last-migration.json");
        Assert.True(File.Exists(receipt));
        using var document = JsonDocument.Parse(File.ReadAllText(receipt));
        Assert.Equal(Target, document.RootElement.GetProperty("resourceRoot").GetString());
        Assert.True(document.RootElement.GetProperty("reclaimedBytes").GetInt64() > 0);
        Assert.Equal(2, document.RootElement.GetProperty("verifiedFiles").GetArrayLength());
        Assert.Equal(64, document.RootElement.GetProperty("verifiedFiles")[0].GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public async Task Installer_cannot_select_an_empty_replacement_for_existing_resources()
    {
        await SeedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => StorageOwnership.InitializeAsync(Target, Location));
        Assert.Equal(Source, Location.ReadRoot());
        Assert.True(File.Exists(UserData));
        Assert.False(Directory.Exists(Target));
    }

    private sealed class InlineProgress(Action<ResourceTransferProgress> report) : IProgress<ResourceTransferProgress>
    {
        public void Report(ResourceTransferProgress value) => report(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
