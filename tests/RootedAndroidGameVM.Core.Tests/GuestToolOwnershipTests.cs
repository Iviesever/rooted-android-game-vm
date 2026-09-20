using System.Diagnostics;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class GuestToolOwnershipTests
{
    [Fact]
    public async Task An_unverified_cleanup_is_not_retried_by_nested_finally_blocks()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-guest-pending-" + Guid.NewGuid().ToString("N"));
        using var service = new AndroidDebugService(InstallPaths.FromProductRoot(root));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var type = typeof(AndroidDebugService).GetNestedType("GuestToolScope", System.Reflection.BindingFlags.NonPublic)!;
        var scope = Activator.CreateInstance(type, nonPublic: true)!;
        var token = (string)type.GetProperty("Token")!.GetValue(scope)!;
        type.GetField("Record")!.SetValue(scope, new GuestToolRecord(token, new string('b', 32), "1:2", "request", "job", 1, 2, "pending", DateTimeOffset.UtcNow));
        type.GetField("CleanupAttempted")!.SetValue(scope, true);
        var holder = typeof(AndroidDebugService).GetField("_guestToolScope", flags)!.GetValue(service)!;
        var value = holder.GetType().GetProperty("Value")!; value.SetValue(holder, scope);
        try
        {
            var method = typeof(AndroidDebugService).GetMethod("CompleteGuestToolsAsync", flags)!;
            var error = await Assert.ThrowsAsync<DebugException>(() => (Task)method.Invoke(service, [true])!);
            Assert.Equal("guest_cleanup_required", error.Code);
            await (Task)method.Invoke(service, [false])!;
            Assert.False(Directory.Exists(root));
        }
        finally { value.SetValue(holder, null); }
    }

    [Theory]
    [InlineData("files.transfer.start", true)]
    [InlineData("files.transfer.plan", true)]
    [InlineData("files.push", true)]
    [InlineData("apps.list", true)]
    [InlineData("users.list", true)]
    [InlineData("files.tools.cleanup", false)]
    [InlineData("input", false)]
    [InlineData("start", false)]
    [InlineData("root-shell", false)]
    public void File_tools_have_a_scope_without_changing_shell_or_input_lifetimes(string command, bool scoped) =>
        Assert.Equal(scoped, GuestToolPolicy.UsesLease(command));

    [Theory]
    [InlineData("../outside")]
    [InlineData("0123456789012345678901234567890;id")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    [InlineData("")]
    public async Task Invalid_cleanup_tokens_fail_before_device_or_file_access(string token)
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-guest-invalid-" + Guid.NewGuid().ToString("N"));
        using var service = new AndroidDebugService(InstallPaths.FromProductRoot(root));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteAsync(DebugRequest.Create("files.tools.cleanup", new { token }), default));
        Assert.False(Directory.Exists(root));
        Assert.Throws<ArgumentException>(() => GuestToolPolicy.Wrap(token, "echo should-not-run"));
    }

    [Fact]
    public void Cleanup_requires_the_recorded_process_start_time_and_preserves_both_evidence_links()
    {
        using var owner = Process.GetCurrentProcess();
        var record = new GuestToolRecord(new string('a', 32), new string('b', 32), "1:2", "req", "job", owner.Id,
            owner.StartTime.ToUniversalTime().Ticks, "active", DateTimeOffset.UtcNow);
        Assert.True(GuestToolPolicy.OwnerAlive(record));
        Assert.False(GuestToolPolicy.OwnerAlive(record with { OwnerStartedTicks = record.OwnerStartedTicks - 1 }));
        var original = new DebugException("device_offline", "disconnected", "uploading");
        original.Data["toolEvidencePath"] = "host-tool.json";
        original.Data["guestCleanupPath"] = "guest-token.json";
        var reply = DebugReply.Failure(new DebugException("device_offline", "transfer interrupted", "uploading", "execution.json", original));
        Assert.Equal("host-tool.json", reply.Error!.ToolEvidencePath);
        Assert.Equal("guest-token.json", reply.Error.GuestCleanupPath);
        Assert.Equal("uploading", reply.Error.Stage);
        var roundTrip = DebugReply.Failure(DebugException.FromError(reply.Error));
        Assert.Equal(reply.Error.GuestCleanupPath, roundTrip.Error!.GuestCleanupPath);
        Assert.Equal(reply.Error.ToolEvidencePath, roundTrip.Error.ToolEvidencePath);
    }
}
