using System.Diagnostics;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class BoundedProcessTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4095)] [InlineData(4096)] [InlineData(4097)] [InlineData(65536)]
    public async Task Text_boundary_preserves_utf16_and_line_endings(int length)
    {
        var text = string.Concat(Enumerable.Repeat("中文\r\n🙂", length / 6 + 1))[..length];
        Assert.Equal(text, await ProcessRunner.ReadBoundedTextAsync(new StringReader(text), length));
        if (length > 0)
            await Assert.ThrowsAsync<ProcessOutputLimitException>(() => ProcessRunner.ReadBoundedTextAsync(new StringReader(text), length - 1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.ReadBoundedTextAsync(new StringReader(text), length, new(true)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Overflow_on_either_pipe_stops_a_long_lived_producer(bool stderr)
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(Path.GetTempPath(), "rgvm-output-pid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var script = $"[IO.File]::WriteAllText($env:RGVM_TEST_PID, [string]$PID); $chunk = 'x' * 8192; while ($true) {{ [Console]::{(stderr ? "Error" : "Out")}.Write($chunk) }}";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var request = new ProcessRequest(Spec(script), EnvironmentVariables: new Dictionary<string, string> { ["RGVM_TEST_PID"] = file }, MaxOutputCharacters: 32768);
            var error = await Assert.ThrowsAsync<ProcessOutputLimitException>(() => new ProcessRunner().RunRequestAsync(request, deadline.Token));
            Assert.Equal("output_limit", DebugReply.Failure(error).Error!.Code);
            AssertExited(int.Parse(await File.ReadAllTextAsync(file)));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Cancelling_a_blocked_stdin_write_reaps_the_child()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(Path.GetTempPath(), "rgvm-stdin-pid-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ProcessResult>? running = null;
        try
        {
            var request = new ProcessRequest(Spec("[IO.File]::WriteAllText($env:RGVM_TEST_PID, [string]$PID); Start-Sleep 60"),
                new string('x', 1024 * 1024), new Dictionary<string, string> { ["RGVM_TEST_PID"] = file });
            running = new ProcessRunner().RunRequestAsync(request, stop.Token);
            while (!File.Exists(file) || new FileInfo(file).Length == 0) await Task.Delay(25, stop.Token);
            var child = int.Parse(await File.ReadAllTextAsync(file));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            AssertExited(child);
        }
        finally
        {
            stop.Cancel();
            if (running is not null) { try { await running; } catch { } }
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Finite_output_and_nonzero_exit_are_preserved_and_invalid_limits_do_not_launch()
    {
        var runner = new ProcessRunner();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunRequestAsync(new(new("missing-tool", []), MaxOutputCharacters: -1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunRequestAsync(new(new("missing-tool", []), MaxOutputCharacters: ProcessRunner.DefaultMaxOutputCharacters + 1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new("missing-tool", []), new(true)));
        if (!OperatingSystem.IsWindows()) return;
        var result = await runner.RunRequestAsync(new(Spec("$inputText = [Console]::ReadLine(); [Console]::Write($inputText); [Console]::Error.Write('detail'); exit 7"), "input\n", MaxOutputCharacters: 6));
        Assert.Equal(new ProcessResult(7, "input", "detail"), result);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Oversized_preview_payload_is_rejected_from_its_length_header()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var header = new MemoryStream(BitConverter.GetBytes(8 * 1024 * 1024 + 1));
        await Assert.ThrowsAsync<IOException>(() => DebugBroker.ReadFrameAsync(header, default, 8 * 1024 * 1024));
    }

    private static ProcessSpec Spec(string script) => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command", script]);
    private static void AssertExited(int pid)
    {
        try { using var child = Process.GetProcessById(pid); Assert.True(child.HasExited, "Owned child must be reaped before returning."); }
        catch (ArgumentException) { }
    }
}
