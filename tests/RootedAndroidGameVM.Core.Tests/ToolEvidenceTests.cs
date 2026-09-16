using System.Diagnostics;
using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ToolEvidenceTests
{
    [Fact]
    public async Task Lifecycle_runner_also_preserves_both_streams_without_changing_exit_contract()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-runner-evidence-" + Guid.NewGuid().ToString("N"));
        DebugOperation.Current.Value = new("lifecycle-request", "lifecycle-job", directory) { Stage = "installing" };
        try
        {
            var result = await new ProcessRunner().RunAsync(Spec("[Console]::Out.Write('install-out'); [Console]::Error.Write('install-err'); exit 9"));
            Assert.Equal(9, result.ExitCode); Assert.Equal("install-out", result.StandardOutput); Assert.Equal("install-err", result.StandardError);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(result.EvidencePath!));
            Assert.Equal("installing", evidence.RootElement.GetProperty("stage").GetString());
            Assert.Equal("install-out", await File.ReadAllTextAsync(evidence.RootElement.GetProperty("stdoutPath").GetString()!));
            Assert.Equal("install-err", await File.ReadAllTextAsync(evidence.RootElement.GetProperty("stderrPath").GetString()!));
        }
        finally { DebugOperation.Current.Value = null; Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Nonzero_tool_exit_retains_both_pipes_and_a_reaped_pid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-tool-evidence-" + Guid.NewGuid().ToString("N"));
        var operation = new DebugOperation("request-fixture", "job-fixture", directory) { Stage = "triggering_import" };
        DebugOperation.Current.Value = operation;
        try
        {
            var error = await Assert.ThrowsAsync<DebugException>(() => BinaryProcess.RunTextAsync(
                Spec("[Console]::Out.Write('partial result'); [Console]::Error.Write('diagnostic'); exit 7"), 4096, default));
            var reply = operation.Complete(DebugReply.Failure(error));
            Assert.Equal("request-fixture", reply.RequestId); Assert.Equal("job-fixture", reply.JobId);
            Assert.Equal("triggering_import", reply.Error!.Stage); Assert.Equal("failed", reply.Terminal);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(reply.Error.ToolEvidencePath!));
            Assert.Equal(7, evidence.RootElement.GetProperty("exitCode").GetInt32());
            Assert.True(evidence.RootElement.GetProperty("reaped").GetBoolean());
            Assert.Equal("partial result", await File.ReadAllTextAsync(evidence.RootElement.GetProperty("stdoutPath").GetString()!));
            Assert.Equal("diagnostic", await File.ReadAllTextAsync(evidence.RootElement.GetProperty("stderrPath").GetString()!));
        }
        finally { DebugOperation.Current.Value = null; Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Cancellation_preserves_partial_text_and_reaps_the_actual_child()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-tool-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "ready");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        DebugOperation.Current.Value = new("cancel-fixture", "job-fixture", directory);
        try
        {
            var script = "[Console]::Out.Write('before cancellation'); [Console]::Out.Flush(); [IO.File]::WriteAllText('" + marker.Replace("'", "''") + "','ready'); Start-Sleep 60";
            var running = BinaryProcess.RunTextAsync(Spec(script), 4096, deadline.Token);
            while (!File.Exists(marker)) await Task.Delay(20, deadline.Token);
            deadline.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            var reply = DebugOperation.Current.Value.Complete(DebugReply.Failure(error));
            Assert.Equal("cancelled", reply.Terminal);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(reply.Error!.ToolEvidencePath!));
            var root = evidence.RootElement;
            Assert.Equal("before cancellation", await File.ReadAllTextAsync(root.GetProperty("stdoutPath").GetString()!));
            Assert.True(root.GetProperty("reaped").GetBoolean());
            try { using var child = Process.GetProcessById(root.GetProperty("pid").GetInt32()); Assert.True(child.HasExited); }
            catch (ArgumentException) { }
        }
        finally { deadline.Cancel(); DebugOperation.Current.Value = null; Directory.Delete(directory, true); }
    }

    private static ProcessSpec Spec(string script) => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-NonInteractive", "-Command", script]);
}
