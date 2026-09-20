using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class TransferExecutionTests
{
    [Fact]
    public void Binary_download_uses_exec_out_instead_of_shell_terminal_translation()
    {
        var spec = RootedAndroidGameVM.Core.Android.AndroidCommandFactory.RootExecOut(
            RootedAndroidGameVM.Core.Android.AndroidSdkLayout.FromRoot(Path.GetTempPath()),
            new("owned", "emulator-5554", 5554, "host", 1024), "cat 'binary-file'");
        Assert.Equal("exec-out", spec.Arguments[2]);
        Assert.DoesNotContain("shell", spec.Arguments);
        Assert.Contains("exec su -c", spec.Arguments[3]);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [Fact]
    public async Task Local_commit_preserves_the_original_and_recovers_a_lost_completion_receipt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-commit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "target.txt"); var stage = Path.Combine(directory, "stage"); var backup = Path.Combine(directory, "backup");
            await File.WriteAllTextAsync(target, "original"); await File.WriteAllTextAsync(stage, "replacement");
            var expected = await FileTransferPolicy.LocalFingerprintAsync(target, default);
            var source = (await FileTransferPolicy.LocalFingerprintAsync(stage, default))!;
            var item = new TransferPlanEntry(0, "s0", "source", "target.txt", source, expected, "different");
            async Task<string?> Commit(bool recovering)
            {
                var method = typeof(AndroidDebugService).GetMethod("CommitLocalTransferAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                return await (Task<string?>)method.Invoke(null, [target, stage, backup, item, expected, recovering, CancellationToken.None])!;
            }
            Assert.Equal(backup, await Commit(false));
            Assert.Equal("replacement", await File.ReadAllTextAsync(target)); Assert.Equal("original", await File.ReadAllTextAsync(backup));
            Assert.Equal(backup, await Commit(true)); Assert.False(File.Exists(stage));
            Assert.Equal("original", await File.ReadAllTextAsync(backup));
        }
        finally { Directory.Delete(directory, true); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [Fact]
    public async Task Local_commit_refuses_a_changed_target_and_can_finish_after_a_backup_rename()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rgvm-commit-fault-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "target.txt"); var stage = Path.Combine(directory, "stage"); var backup = Path.Combine(directory, "backup");
            await File.WriteAllTextAsync(target, "original"); await File.WriteAllTextAsync(stage, "replacement");
            var expected = await FileTransferPolicy.LocalFingerprintAsync(target, default);
            var item = new TransferPlanEntry(0, "s0", "source", "target.txt", (await FileTransferPolicy.LocalFingerprintAsync(stage, default))!, expected, "different");
            async Task<string?> Commit(bool recovering)
            {
                var method = typeof(AndroidDebugService).GetMethod("CommitLocalTransferAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                return await (Task<string?>)method.Invoke(null, [target, stage, backup, item, expected, recovering, CancellationToken.None])!;
            }
            await File.WriteAllTextAsync(target, "concurrent change");
            Assert.Equal("target_changed", (await Assert.ThrowsAsync<DebugException>(() => Commit(false))).Code);
            Assert.Equal("concurrent change", await File.ReadAllTextAsync(target)); Assert.False(File.Exists(backup));
            await File.WriteAllTextAsync(target, "original"); File.Move(target, backup);
            Assert.Equal(backup, await Commit(true)); Assert.Equal("replacement", await File.ReadAllTextAsync(target));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Lost_execution_owner_is_reported_interrupted_without_replaying_it()
    {
        var header = new TransferExecutionHeader(new string('a', 32), new("skip", false), "session", "job", "running", DateTimeOffset.UtcNow,
            OwnerPid: Environment.ProcessId, OwnerStartedTicks: 1);
        Assert.Equal("interrupted", TransferExecutionLiveness.Observe(header).Status);
        Assert.Equal("succeeded", TransferExecutionLiveness.Observe(header with { Status = "succeeded" }).Status);
        Assert.True(TransferExecutionLiveness.CanResume(TransferExecutionLiveness.Observe(header)));
        Assert.False(TransferExecutionLiveness.CanResume(header with { Status = "failed", ErrorCode = "plan_stale" }));
        Assert.True(TransferExecutionLiveness.CanResume(header with { Status = "failed", ErrorCode = "device_offline" }));
    }
    [Fact]
    public void Duplicate_requests_keep_job_identity_but_changed_options_are_detectable()
    {
        var id = new string('a', 32);
        var first = DebugRequest.Create("files.transfer.start", new { planId = id, idempotencyKey = "one", conflictPolicy = "skip" });
        var reordered = DebugRequest.Create("files.transfer.start", new { conflictPolicy = "skip", idempotencyKey = "one", planId = id }) with { RequestId = "another-request" };
        var changed = DebugRequest.Create("files.transfer.start", new { planId = id, idempotencyKey = "one", conflictPolicy = "overwrite" });
        Assert.Equal(TransferDispatchIdentity.JobId(first), TransferDispatchIdentity.JobId(reordered));
        Assert.Equal(TransferDispatchIdentity.Intent(first), TransferDispatchIdentity.Intent(reordered));
        Assert.Equal(TransferDispatchIdentity.JobId(first), TransferDispatchIdentity.JobId(changed));
        Assert.NotEqual(TransferDispatchIdentity.Intent(first), TransferDispatchIdentity.Intent(changed));
        Assert.NotEqual(TransferDispatchIdentity.JobId(first), TransferDispatchIdentity.JobId(first with { Command = "files.transfer.resume" }));
        Assert.Throws<ArgumentException>(() => TransferDispatchIdentity.JobId(DebugRequest.Create("files.transfer.start", new { planId = "../bad", idempotencyKey = "one" })));
    }

    [Fact]
    public async Task Append_journal_recovers_only_complete_updates_and_repairs_an_interrupted_tail()
    {
        var path = Path.Combine(Path.GetTempPath(), "rgvm-execution-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var store = new TransferExecutionStore(path);
            await store.SaveItemAsync(new(0, "staging", "中文/a", 8), default);
            await store.SaveItemAsync(new(0, "staging", "中文/a", 16), default);
            await File.AppendAllTextAsync(store.EntriesPath, "{\"index\":1,");
            var restored = store.ReadEntries(repairTail: true);
            Assert.Single(restored); Assert.Equal(16, restored[0].Offset);
            await store.SaveItemAsync(new(1, "completed", "b", 4), default);
            Assert.Equal(2, store.ReadEntries().Count);
            Assert.EndsWith("\n", await File.ReadAllTextAsync(store.EntriesPath));
            Assert.All((await File.ReadAllLinesAsync(store.EntriesPath)), line => { using var parsed = JsonDocument.Parse(line); Assert.True(parsed.RootElement.TryGetProperty("index", out _)); });
        }
        finally { Directory.Delete(path, true); }
    }
}
