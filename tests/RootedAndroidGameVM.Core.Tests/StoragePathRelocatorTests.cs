using System.Text.Json;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class StoragePathRelocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rgvm-path-tests", Guid.NewGuid().ToString("N"));
    private InstallPaths Source => InstallPaths.FromProductRoot(Path.Combine(_root, "old"));
    private InstallPaths Target => InstallPaths.FromProductRoot(Path.Combine(_root, "新 位置"));
    private string OldBase => Path.Combine(Source.AvdHome, "test.avd", "base.img");
    private string NewBase => Path.Combine(Target.AvdHome, "test.avd", "base.img");

    [Fact]
    public async Task Metadata_updates_owned_absolute_paths_and_preserves_other_values()
    {
        Directory.CreateDirectory(Target.AvdHome);
        var registration = Path.Combine(Target.AvdHome, "test.ini");
        File.WriteAllText(registration, $"path={Source.AvdHome}\\test.avd\ntarget=android-35\n");
        File.WriteAllText(Path.Combine(Target.ProductRoot, "install.json"), JsonSerializer.Serialize(new
        {
            sdkRoot = Source.SdkRoot, avdHome = Source.AvdHome, version = "0.1.2", description = Source.ProductRoot + "-unrelated"
        }));
        await new StoragePathRelocator().RelocateAsync(Source, Target);
        Assert.Contains("path=" + Path.Combine(Target.AvdHome, "test.avd"), File.ReadAllText(registration));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Target.ProductRoot, "install.json")));
        Assert.Equal(Target.SdkRoot, json.RootElement.GetProperty("sdkRoot").GetString());
        Assert.Equal("0.1.2", json.RootElement.GetProperty("version").GetString());
        Assert.Equal(Source.ProductRoot + "-unrelated", json.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Relative_backing_reference_keeps_the_disk_bytes_unchanged()
    {
        SeedImages();
        var runner = new ImageRunner("base.img");
        await new StoragePathRelocator(runner).RelocateAsync(Source, Target);
        Assert.Empty(runner.Rebases);
        Assert.Equal("qcow-test", File.ReadAllText(Path.Combine(Target.AvdHome, "test.avd", "disk.qcow2")));
    }

    [Fact]
    public async Task Absolute_backing_reference_maps_to_the_verified_new_copy()
    {
        SeedImages();
        var runner = new ImageRunner(OldBase);
        await new StoragePathRelocator(runner).RelocateAsync(Source, Target);
        var rebase = Assert.Single(runner.Rebases);
        Assert.Equal(["rebase", "-u", "-f", "qcow2", "-F", "raw", "-b", NewBase,
            Path.Combine(Target.AvdHome, "test.avd", "disk.qcow2")], rebase.Arguments);
    }

    [Fact]
    public async Task Mismatched_backing_copy_is_rejected_before_any_header_change()
    {
        SeedImages();
        File.WriteAllText(NewBase, "corrupt copy");
        var runner = new ImageRunner(OldBase);
        await Assert.ThrowsAsync<InvalidDataException>(() => new StoragePathRelocator(runner).RelocateAsync(Source, Target));
        Assert.Empty(runner.Rebases);
    }

    [Fact]
    public async Task External_backing_file_is_never_adopted_or_modified()
    {
        SeedImages();
        var external = Path.Combine(_root, "personal.img");
        File.WriteAllText(external, "personal data");
        var runner = new ImageRunner(external);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StoragePathRelocator(runner).RelocateAsync(Source, Target));
        Assert.Empty(runner.Rebases);
        Assert.Equal("personal data", File.ReadAllText(external));
    }

    private void SeedImages()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OldBase)!);
        Directory.CreateDirectory(Path.GetDirectoryName(NewBase)!);
        Directory.CreateDirectory(Path.Combine(Target.SdkRoot, "emulator"));
        File.WriteAllText(Path.Combine(Target.SdkRoot, "emulator", "qemu-img.exe"), "test executable placeholder");
        File.WriteAllText(OldBase, "identical base image");
        File.WriteAllText(NewBase, "identical base image");
        File.WriteAllText(Path.Combine(Target.AvdHome, "test.avd", "disk.qcow2"), "qcow-test");
    }

    private sealed class ImageRunner(string backing) : IProcessRunner
    {
        public List<ProcessSpec> Rebases { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            if (spec.Arguments[0] == "rebase")
            {
                Rebases.Add(spec);
                return Task.FromResult(new ProcessResult(0, "", ""));
            }
            var json = spec.Arguments[^1].EndsWith(".qcow2", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["format"] = "qcow2", ["backing-filename"] = backing, ["backing-filename-format"] = "raw"
                })
                : "{\"format\":\"raw\"}";
            return Task.FromResult(new ProcessResult(0, json, ""));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
