using System.Text;
using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class DiagnosticStreamingTests
{
    private static ProcessSpec Spec(string script) => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
    [Fact]
    public async Task Streaming_nonzero_exit_preserves_raw_output_and_stderr()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-log-evidence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        DebugOperation.Current.Value = new("request", "job", directory);
        try
        {
            var raw = Path.Combine(directory, "raw.log"); var lines = new List<string>();
            var error = await Assert.ThrowsAsync<DebugException>(() => BinaryProcess.RunLinesToFileAsync(
                Spec("[Console]::Out.WriteLine('first'); [Console]::Error.Write('diagnostic'); exit 7"), raw, 4096, line => { lines.Add(line); return Task.CompletedTask; }, default));
            Assert.Equal("tool_failed", error.Code); Assert.Equal(["first"], lines);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync((string)error.Data["toolEvidencePath"]!));
            Assert.Equal(raw, evidence.RootElement.GetProperty("stdoutPath").GetString()); Assert.Equal(7, evidence.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("first", await File.ReadAllTextAsync(raw));
            Assert.Equal("diagnostic", await File.ReadAllTextAsync(evidence.RootElement.GetProperty("stderrPath").GetString()!));
        }
        finally { DebugOperation.Current.Value = null; Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task Cancelled_binary_diagnostics_keep_partial_bytes_and_an_explicit_failure_receipt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-trace-evidence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        DebugOperation.Current.Value = new("request", "job", directory);
        using var stop = new CancellationTokenSource();
        try
        {
            var path = Path.Combine(directory, "partial.trace"); var ready = Path.Combine(directory, "ready");
            var script = "$output = [Console]::OpenStandardOutput(); $bytes = [byte[]](0,10,13,255); $output.Write($bytes,0,$bytes.Length); $output.Flush(); [Console]::Error.Write('before-cancel'); [IO.File]::WriteAllText('" + ready.Replace("'", "''") + "','ready'); Start-Sleep 30";
            var work = BinaryProcess.RunToArtifactAsync(Spec(script), path, 4096, stop.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready)) await Task.Delay(20, deadline.Token);
            stop.Cancel(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.Equal(new byte[] { 0, 10, 13, 255 }, await File.ReadAllBytesAsync(path));
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync((string)error.Data["toolEvidencePath"]!));
            Assert.True(evidence.RootElement.GetProperty("cancelled").GetBoolean()); Assert.True(evidence.RootElement.GetProperty("reaped").GetBoolean());
            Assert.Equal(path, evidence.RootElement.GetProperty("stdoutPath").GetString());
        }
        finally { stop.Cancel(); DebugOperation.Current.Value = null; Directory.Delete(directory, true); }
    }
}
