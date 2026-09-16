using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class MemoryAuditTests
{
    [Fact]
    public void Custom_admission_keeps_commit_and_guest_constraints_and_roundtrips()
    {
        var host = new HostMemorySnapshot(16111, 4096, 32, 75, 6656);
        Assert.True(VmMemoryPolicy.Assess(3072, host, 4096).Allowed);
        Assert.False(VmMemoryPolicy.Assess(3072, host with { AvailableMb = 4095 }, 4096).Allowed);
        Assert.False(VmMemoryPolicy.Assess(3072, host with { AvailableCommitMb = 6655 }, 4096).Allowed);
        Assert.False(VmMemoryPolicy.Assess(3072, host with { AvailableCommitMb = null }, 4096).Allowed);
        Assert.False(VmMemoryPolicy.Assess(8192, host with { AvailableCommitMb = 20000 }, 4096).Allowed);
        Assert.Throws<ArgumentException>(() => VmMemoryPolicy.Assess(3072, host, 1));
        var profile = RuntimeProfile.Recommended with { StartAvailableMb = 4096 };
        Assert.Equal(profile, RuntimeProfile.FromAvdSettings(profile.ToAvdSettings().Select(k => k.Key + "=" + k.Value)));
        Assert.Equal(0, RuntimeProfile.FromAvdSettings([]).StartAvailableMb);
        Assert.True(new MemoryPressureTracker().ShouldStop(host with { AvailableMb = 500 }, TimeSpan.Zero));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(16384)] [InlineData(16385)] [InlineData(65536)]
    public async Task Bounded_pooled_and_streamed_output_is_exact(int size)
    {
        var data = Enumerable.Range(0, size).Select(i => (byte)(i * 31)).ToArray();
        Assert.Equal(data, await BinaryProcess.ReadBoundedAsync(new MemoryStream(data), size, default));
        using var output = new MemoryStream();
        Assert.Equal(size, await BinaryProcess.CopyBoundedAsync(new MemoryStream(data), output, size, default));
        Assert.Equal(data, output.ToArray());
        if (size > 0)
        {
            Assert.Equal("output_limit", (await Assert.ThrowsAsync<DebugException>(() => BinaryProcess.ReadBoundedAsync(new MemoryStream(data), size - 1, default))).Code);
            await Assert.ThrowsAsync<DebugException>(() => BinaryProcess.CopyBoundedAsync(new MemoryStream(data), Stream.Null, size - 1, default));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BinaryProcess.CopyBoundedAsync(new MemoryStream(data), Stream.Null, size, new(true)));
    }
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Stored_results_preserve_small_payloads_and_reference_large_payloads()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "rgvm-result-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var small = await StoredJobResult.WriteAsync(root, "small", new(true, new { value = "完整中文" }));
            Assert.Equal("完整中文", ((JsonElement)small.Read().Result!).GetProperty("value").GetString());
            var large = await StoredJobResult.WriteAsync(root, "large", new(true, new { stdout = new string('x', StoredJobResult.MaxInlineBytes + 1) }));
            var reference = JsonSerializer.SerializeToElement(large.Read(), DebugJson.Options);
            Assert.False(reference.GetProperty("result").GetProperty("inline").GetBoolean());
            using var original = JsonDocument.Parse(File.ReadAllText(large.Path));
            Assert.Equal(StoredJobResult.MaxInlineBytes + 1, original.RootElement.GetProperty("result").GetProperty("stdout").GetString()!.Length);
            var failed = await StoredJobResult.WriteAsync(root, "failed", DebugReply.Failure(new DebugException("probe", "失败")));
            Assert.False(failed.Read().Ok); Assert.Equal("probe", failed.Read().Error!.Code);
            var bigError = await StoredJobResult.WriteAsync(root, "big-error", DebugReply.Failure(new DebugException("output_limit", new string('e', StoredJobResult.MaxInlineBytes + 1))));
            Assert.False(bigError.Read().Ok); Assert.Equal("output_limit", bigError.Read().Error!.Code);
            Assert.True(bigError.Read().Error!.Message.Length < 4200);
            await Assert.ThrowsAsync<ArgumentException>(() => StoredJobResult.WriteAsync(root, "../escape", new(true)));
            File.AppendAllText(small.Path, "corrupt");
            Assert.Throws<IOException>(() => small.Read());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Request_limit_is_checked_before_allocating_a_payload()
    {
        if (!OperatingSystem.IsWindows()) return;
        var stream = new MemoryStream(BitConverter.GetBytes(1024 * 1024 + 1));
        await Assert.ThrowsAsync<IOException>(() => DebugBroker.ReadFrameAsync(stream, default, 1024 * 1024));
    }
    [Fact]
    public void Memory_ownership_rejects_other_avd_or_port()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = InstallPaths.FromProductRoot(Path.Combine(Path.GetTempPath(), "owned-probe"));
        var options = new AndroidVmOptions("vm", "emulator-5554", 5554, "host", 3072);
        var identity = new HostProcessIdentity(1, 0, "qemu.exe", 1, "vm", Path.Combine(paths.AvdHome, "vm.avd"), 5554);
        Assert.True(ProcessMemory.BelongsToVm(identity, paths, options));
        Assert.False(ProcessMemory.BelongsToVm(identity with { ConsolePort = 5556 }, paths, options));
        Assert.False(ProcessMemory.BelongsToVm(identity with { AvdDirectory = paths.ProductRoot }, paths, options));
    }
    [Fact]
    public async Task Streamed_child_errors_cancel_and_stderr_overflow_leave_no_valid_artifact()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "rgvm-stream-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        ProcessSpec Spec(string script) => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command", script]);
        var file = Path.Combine(root, "trace.bin");
        try
        {
            Assert.Equal(4, await BinaryProcess.RunToFileAsync(Spec("[Console]::Write('test')"), file, 4, default));
            Assert.Equal("test", File.ReadAllText(file)); File.Delete(file);
            await Assert.ThrowsAsync<DebugException>(() => BinaryProcess.RunToFileAsync(Spec("[Console]::Write('invalid'); exit 2"), file, 32, default));
            Assert.Empty(Directory.GetFiles(root));
            await Assert.ThrowsAsync<DebugException>(() => BinaryProcess.RunToFileAsync(Spec("[Console]::Write('too large')"), file, 3, default));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var error = await Assert.ThrowsAsync<DebugException>(() => BinaryProcess.RunToFileAsync(Spec("[Console]::Error.Write(('x' * 1100000)); Start-Sleep 60"), file, 32, deadline.Token));
            Assert.Equal("output_limit", error.Code); Assert.Empty(Directory.GetFiles(root));
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BinaryProcess.RunToFileAsync(Spec("Start-Sleep 60"), file, 32, cancel.Token));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
