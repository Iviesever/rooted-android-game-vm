using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class DebugProtocolRecoveryTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Oversized_quick_reply_becomes_a_complete_file_reference_instead_of_a_broken_pipe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-wire-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reply = new DebugReply(true, new { raw = new string('x', DebugWireReply.MaximumBytes + 1) }, RequestId: "quick-request", Stage: "metrics", Terminal: "succeeded");
            var bytes = await DebugWireReply.SerializeAsync(reply, directory, default);
            Assert.True(bytes.Length < 4096);
            var reference = JsonSerializer.Deserialize<DebugReply>(bytes, DebugJson.Options)!;
            Assert.Equal("quick-request", reference.RequestId); Assert.Equal("metrics", reference.Stage);
            Assert.Equal("succeeded", reference.Terminal);
            var file = ((JsonElement)reference.Result!).GetProperty("resultPath").GetString()!;
            using var stored = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal(DebugWireReply.MaximumBytes + 1, stored.RootElement.GetProperty("result").GetProperty("raw").GetString()!.Length);
            var small = await DebugWireReply.SerializeAsync(new(true, new { unchanged = true }), directory, default);
            using var normal = JsonDocument.Parse(small);
            Assert.True(normal.RootElement.GetProperty("result").GetProperty("unchanged").GetBoolean());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Large_progress_stays_on_disk_and_retains_import_recovery_identity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-progress-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var operation = new DebugOperation("request", "job", directory);
            var progress = operation.CaptureProgress(new
            {
                stage = "verifying_content",
                importId = "original-import",
                directory,
                missingFiles = Enumerable.Range(0, 4096).Select(i => new string('x', 40) + i).ToArray()
            });
            var summary = JsonSerializer.SerializeToElement(progress);
            Assert.True(DebugJson.Write(progress).Length < 1024);
            Assert.Equal("original-import", summary.GetProperty("importId").GetString());
            using var original = JsonDocument.Parse(File.ReadAllText(summary.GetProperty("detailPath").GetString()!));
            Assert.Equal(4096, original.RootElement.GetProperty("missingFiles").GetArrayLength());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Legacy_requests_still_parse_and_new_ids_roundtrip()
    {
        var old = JsonSerializer.Deserialize<DebugRequest>("{\"command\":\"status\"}", DebugJson.Options)!;
        Assert.Equal(1, old.SchemaVersion); Assert.Null(old.RequestId);
        var current = old with { RequestId = "codex:retry-1" };
        Assert.Equal(current, JsonSerializer.Deserialize<DebugRequest>(DebugJson.Write(current), DebugJson.Options));
        using var result = JsonDocument.Parse(DebugJson.Write(new DebugOperation(current.RequestId!, "job", "unused").Complete(new(true, new { value = 3 }))));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(3, result.RootElement.GetProperty("result").GetProperty("value").GetInt32());
        Assert.Equal(current.RequestId, result.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public void Persisted_unfinished_work_is_interrupted_with_original_identity_and_recovery_evidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-journal-test-" + Guid.NewGuid().ToString("N"));
        var record = new DebugJobJournal(Guid.NewGuid().ToString("N"), "original-request", "files.push", DateTimeOffset.UtcNow,
            false, "waiting_for_unpack", "vm-session", "1234", directory, null, null, null, null,
            JsonSerializer.SerializeToElement(new { importId = "recover-this-import", remote = "/same-transferred-file" }), 42, 100);
        try
        {
            DebugJobJournalStore.Save(directory, record);
            var saved = Assert.Single(DebugJobJournalStore.Load(directory));
            Assert.Equal(record.RequestId, saved.RequestId); Assert.Equal(record.JobId, saved.JobId);
            Assert.Equal(42, saved.BrokerPid); Assert.Equal(100, saved.BrokerStartedAtUtcTicks);
            Assert.Equal("recover-this-import", saved.Progress!.Value.GetProperty("importId").GetString());
            var failure = DebugJobJournalStore.Interrupted(saved, Path.Combine(directory, record.JobId + ".json"));
            Assert.False(failure.Ok); Assert.Equal("interrupted", failure.Terminal);
            Assert.Equal("waiting_for_unpack", failure.Error!.Stage); Assert.Equal(record.Session, failure.Session);
            Assert.Equal("original-request", failure.RequestId); Assert.NotNull(failure.Error.EvidencePath);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Large_result_reference_preserves_request_job_stage_and_terminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-large-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var operation = new DebugOperation("request-large", "job-large", directory) { Stage = "shell", Session = "vm", Pid = "12" };
            var reply = operation.Complete(new(true, new { stdout = new string('x', StoredJobResult.MaxInlineBytes + 1) }));
            var stored = await StoredJobResult.WriteAsync(directory, "large", reply);
            var reference = stored.Read();
            Assert.Equal(reply.RequestId, reference.RequestId); Assert.Equal(reply.JobId, reference.JobId);
            Assert.Equal("succeeded", reference.Terminal); Assert.Equal("shell", reference.Stage);
            Assert.Equal("vm", reference.Session); Assert.Equal("12", reference.Pid);
            Assert.Null(stored.Envelope!.Result);
            Assert.Contains("resultPath", DebugJson.Write(reference.Result!));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("cancelled", "cancelled")]
    [InlineData("timeout", "timed_out")]
    [InlineData("interrupted", "interrupted")]
    [InlineData("device_offline", "failed")]
    public void Terminal_state_never_collapses_cancellation_timeout_and_failure(string code, string terminal)
    {
        var operation = new DebugOperation("request", "job", "unused") { Stage = "waiting_for_app" };
        var reply = operation.Complete(new(false, Error: new(code, "reason", "waiting_for_unpack", "evidence.json")));
        Assert.Equal(terminal, reply.Terminal); Assert.Equal("waiting_for_unpack", reply.Stage);
        Assert.Equal("evidence.json", reply.Error!.EvidencePath);
    }
}
