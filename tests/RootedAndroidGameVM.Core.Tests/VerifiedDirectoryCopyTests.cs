using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class VerifiedDirectoryCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rgvm-copy-tests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Destination => Path.Combine(_root, "destination");

    private VerifiedDirectoryCopy CreateCopier() => new();

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(Source, "runtime", "empty"));
        File.WriteAllBytes(Path.Combine(Source, "runtime", "userdata.img"), Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray());
        File.WriteAllText(Path.Combine(Source, "install.json"), "dummy-install-marker");
    }

    [Fact]
    public async Task Copy_preserves_source_and_verifies_every_destination_file()
    {
        Seed();
        var manifest = await CreateCopier().CopyAsync(Source, Destination, null, null, CancellationToken.None);
        Assert.Equal(2, manifest.Files.Count);
        Assert.True(Directory.Exists(Path.Combine(Destination, "runtime", "empty")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "runtime", "userdata.img")),
            File.ReadAllBytes(Path.Combine(Destination, "runtime", "userdata.img")));
        Assert.Equal("dummy-install-marker", File.ReadAllText(Path.Combine(Source, "install.json")));
    }

    [Fact]
    public async Task Nonempty_destination_is_never_overwritten()
    {
        Seed();
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Destination, "keep.txt"), "existing user data");
        var copier = CreateCopier();
        await Assert.ThrowsAsync<IOException>(async () => await copier.CopyAsync(Source, Destination, null, null, CancellationToken.None));
        Assert.Equal("existing user data", File.ReadAllText(Path.Combine(Destination, "keep.txt")));
    }

    [Fact]
    public async Task Nested_target_is_rejected_before_copying()
    {
        Seed();
        var target = Path.Combine(Source, "nested");
        var copier = CreateCopier();
        await Assert.ThrowsAsync<ArgumentException>(async () => await copier.CopyAsync(Source, target, null, null, CancellationToken.None));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Cancelled_copy_cannot_delete_source_files()
    {
        Seed();
        var copier = CreateCopier();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await copier.CopyAsync(Source, Destination, null, null, new CancellationToken(true)));
        Assert.True(File.Exists(Path.Combine(Source, "runtime", "userdata.img")));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task Fixed_control_files_can_be_excluded_without_excluding_nested_data()
    {
        Seed();
        File.WriteAllText(Path.Combine(Source, "storage-location.json"), "control");
        File.WriteAllText(Path.Combine(Source, "runtime", "storage-location.json"), "application data");
        await CreateCopier().CopyAsync(Source, Destination,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "storage-location.json" }, null, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(Destination, "storage-location.json")));
        Assert.Equal("application data", File.ReadAllText(Path.Combine(Destination, "runtime", "storage-location.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
