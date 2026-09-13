using System.Formats.Tar;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DebugWorkbenchTests
{
    [Theory]
    [InlineData("../escaped.lua")]
    [InlineData("/absolute.lua")]
    [InlineData("C:/outside.lua")]
    public void Malody_archives_are_rejected_before_transfer_for_path_escape(string entryName)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".msp");
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(file, System.IO.Compression.ZipArchiveMode.Create))
            { using var writer = new StreamWriter(zip.CreateEntry(entryName).Open()); writer.Write("test"); }
            Assert.Throws<InvalidDataException>(() => ImportArchivePolicy.Validate(file));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public void Restored_avd_gpu_configuration_takes_precedence_over_stale_ui_preference()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-profile-" + Guid.NewGuid().ToString("N"));
        var avd = Path.Combine(root, "runtime", "avd", "rooted_android_game_vm_api35.avd"); Directory.CreateDirectory(avd);
        try
        {
            File.WriteAllText(Path.Combine(root, "performance-profile.txt"), "HighPerformance");
            File.WriteAllText(Path.Combine(avd, "config.ini"), "hw.gpu.mode=swiftshader_indirect\n");
            Assert.Equal(PerformanceProfile.Stable, PerformanceProfileService.ReadCurrent(root));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Insufficient_space_is_rejected_before_modifying_any_files()
    {
        Assert.Equal("disk_full", Assert.Throws<DebugException>(() => ColdCheckpoint.RequireSpace(Path.GetTempPath(), long.MaxValue)).Code);
    }
    [Fact]
    public void Release_only_owns_live_contacts_and_keeps_their_last_coordinates()
    {
        var ledger = new TouchLedger(); ledger.Apply([new(0, 100, 200), new(5, 300, 400)]);
        ledger.Apply([new(0, 150, 220), new(5, 300, 400, 0)]);
        Assert.Equal([new TouchPoint(0, 150, 220, 0)], ledger.Releases());
        ledger.Apply(ledger.Releases()); Assert.Empty(ledger.Releases());
    }
    [Fact]
    public void Log_loss_is_not_confused_with_application_network_or_audio_errors()
    {
        Assert.False(LogClassification.IsLossMarker("Unity: connection dropped"));
        Assert.False(LogClassification.IsLossMarker("AudioTrack: dropped buffer"));
        Assert.True(LogClassification.IsLossMarker("chatty : uid=10123 identical 200 lines"));
        Assert.Equal("anr", LogClassification.Category("ActivityManager: ANR in test.app"));
        Assert.Equal("crash", LogClassification.Category("AndroidRuntime: FATAL EXCEPTION: main"));
    }
    private static ScreenObservation Screen => new("one", "session", "image.png", "emulator-grpc", 2400, 1080, 90, 0, "test.app", true, true, false, DateTimeOffset.UtcNow);
    [Fact]
    public void Landscape_screenshot_maps_back_to_physical_touch_panel()
    {
        var screen = Screen with { ImageRotation = 90 };
        Assert.Equal(new TouchPoint(4, 284, 1370), AndroidDebugService.ToNativeTouch(new(4, 1370, 795), screen));
        Assert.Equal(new TouchPoint(2, 1079, 0, 0), AndroidDebugService.ToNativeTouch(new(2, 0, 0, 0), screen));
    }
    [Theory]
    [InlineData("../private")]
    [InlineData("/data/system")]
    [InlineData("one/../../two")]
    [InlineData("one\\two")]
    public void File_scope_rejects_escape_before_running_adb(string path) => Assert.Throws<ArgumentException>(() => AndroidDebugService.RemotePath("external", "test.app", path));
    [Fact]
    public void Host_identity_rejects_same_name_in_other_directory_or_port()
    {
        var o = new AndroidVmOptions("owned", "emulator-5554", 5554, "host", 4096);
        var host = new HostProcessIdentity(42, 1, "qemu.exe", 100, "owned", @"D:\owned\owned.avd", 5554);
        Assert.True(OwnedInstance.Matches(host, @"D:\owned\owned.avd", o));
        Assert.False(OwnedInstance.Matches(host with { AvdDirectory = @"D:\other\owned.avd" }, @"D:\owned\owned.avd", o));
        Assert.False(OwnedInstance.Matches(host with { ConsolePort = 5556 }, @"D:\owned\owned.avd", o));
        Assert.False(OwnedInstance.Matches(host with { AvdName = "studio" }, @"D:\owned\owned.avd", o));
    }
    [Fact]
    public void Six_independent_downs_then_releases_are_valid()
    {
        var down = Enumerable.Range(0, 6).Select(i => new TouchPoint(i, 100 + 300 * i, 900)).ToArray();
        AndroidDebugService.ValidateFrames([new(0, down), .. down.Select((p, i) => new InputFrame(500 + i * 100, [p with { Pressure = 0 }]))], Screen);
    }
    [Fact]
    public void Duplicate_slots_outside_pixels_and_reversed_time_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => AndroidDebugService.ValidateFrames([new(0, [new(0, 1, 1), new(0, 2, 2)])], Screen));
        Assert.Throws<ArgumentException>(() => AndroidDebugService.ValidateFrames([new(0, [new(10, 1, 1)])], Screen));
        Assert.Throws<ArgumentException>(() => AndroidDebugService.ValidateFrames([new(0, [new(0, 2400, 1)])], Screen));
        Assert.Throws<ArgumentException>(() => AndroidDebugService.ValidateFrames([new(200, []), new(100, [])], Screen));
        Assert.Throws<ArgumentException>(() => AndroidDebugService.ValidateFrames([new(120001, [])], Screen));
    }
    [Fact]
    public async Task Binary_channel_preserves_all_byte_values_and_enforces_limit()
    {
        var data = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        Assert.Equal(data, await BinaryProcess.ReadBoundedAsync(new MemoryStream(data), 4096, default));
        await Assert.ThrowsAsync<DebugException>(() => BinaryProcess.ReadBoundedAsync(new MemoryStream(data), 4095, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BinaryProcess.ReadBoundedAsync(new MemoryStream(data), 4096, cancelled.Token));
    }
    [Fact]
    public async Task Pipe_protocol_rejects_oversize_and_partial_frame()
    {
        await Assert.ThrowsAsync<IOException>(() => DebugBroker.ReadFrameAsync(new MemoryStream(BitConverter.GetBytes(17 * 1024 * 1024)), default));
        await Assert.ThrowsAsync<EndOfStreamException>(() => DebugBroker.ReadFrameAsync(new MemoryStream([10, 0, 0, 0, 1]), default));
    }
    [Fact]
    public void Grpc_failure_never_returns_authentication_material()
    {
        var error = new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unauthenticated, "Bearer should-not-appear"));
        Assert.DoesNotContain("should-not-appear", DebugJson.Write(DebugReply.Failure(error)));
    }
    [Fact]
    public void Failed_checkpoint_switch_restores_original_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-switch-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "current"); var staged = Path.Combine(root, "stage"); var rollback = Path.Combine(root, "rollback");
        Directory.CreateDirectory(current); Directory.CreateDirectory(staged); File.WriteAllText(Path.Combine(current, "disk"), "original");
        try
        {
            Assert.Throws<IOException>(() => ColdCheckpoint.SwitchDirectories(current, staged, rollback, (a, b) =>
            { if (a == staged) throw new IOException("Injected switch failure"); Directory.Move(a, b); }));
            Assert.Equal("original", File.ReadAllText(Path.Combine(current, "disk")));
            Assert.True(Directory.Exists(staged)); Assert.False(Directory.Exists(rollback));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("../escape", false)]
    [InlineData("innocent", true)]
    public void Private_export_rejects_tar_escape_and_links(string name, bool link)
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-tar-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var tar = Path.Combine(root, "bad.tar");
            using (var stream = File.Create(tar)) using (var writer = new TarWriter(stream))
            {
                var entry = new PaxTarEntry(link ? TarEntryType.SymbolicLink : TarEntryType.RegularFile, name);
                if (link) entry.LinkName = "../../secret"; else entry.DataStream = new MemoryStream([1, 2, 3]);
                writer.WriteEntry(entry);
            }
            Assert.Throws<IOException>(() => SafeTarExtractor.Extract(tar, Path.Combine(root, "result")));
            Assert.False(File.Exists(Path.Combine(root, "escape")));
        }
        finally { Directory.Delete(root, true); }
    }
}
