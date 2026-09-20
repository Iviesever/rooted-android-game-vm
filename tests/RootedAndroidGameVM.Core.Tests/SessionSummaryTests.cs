using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class SessionSummaryTests
{
    [Theory]
    [InlineData(Grpc.Core.StatusCode.Cancelled, "cancelled")]
    [InlineData(Grpc.Core.StatusCode.DeadlineExceeded, "timeout")]
    [InlineData(Grpc.Core.StatusCode.Unavailable, "grpc_unavailable")]
    public void Input_transport_errors_keep_category_without_exposing_authentication_detail(Grpc.Core.StatusCode status, string expected)
    {
        var reply = DebugReply.Failure(new Grpc.Core.RpcException(new(status, "Bearer private-material")));
        Assert.Equal(expected, reply.Error!.Code); Assert.DoesNotContain("private-material", DebugJson.Write(reply));
    }
    [Theory]
    [InlineData("100:200", "100:201")]
    [InlineData("100:200", "101:200")]
    public void Input_and_cleanup_reject_reused_pid_or_a_different_instance(string expected, string actual)
    {
        var error = Assert.Throws<DebugException>(() => InputSessionPolicy.RequireSame(expected, actual));
        Assert.Equal("instance_mismatch", error.Code); Assert.Equal("input_session_check", error.Stage);
        InputSessionPolicy.RequireSame(expected, expected);
    }

    [Fact]
    public void No_application_selection_is_visible_in_the_shared_summary()
    {
        var summary = new SessionSummary(DateTimeOffset.UtcNow, "emulator-5554", "Running", "session", "",
            null, null, [], null, null, [], "artifacts", [], "选择应用", null, "");
        Assert.Contains("App：未选择应用", SessionSummaryText.Render(summary));
        Assert.Null(summary.ResumeRequest);
    }

    [Fact]
    public void Common_text_exposes_observation_time_unknown_page_and_unconfirmed_release()
    {
        var at = DateTimeOffset.Parse("2026-09-16T12:34:56Z");
        var summary = new SessionSummary(at, "emulator-5554", "Running", "vm-session", "test.app",
            new("test.app", "42", "test.app", true, false, "activity_ready", at, "activity.txt"), null, [], null,
            new("unverified", false, "vm-session", at, [1, 5], "grpc_unavailable", "release.json"), [1, 5], "artifacts", [], "release then observe", null, "");
        var text = SessionSummaryText.Render(summary);
        Assert.Contains("PID 42", text); Assert.Contains("12:34:56", text);
        Assert.Contains("页面：未验证", text); Assert.Contains("释放未确认", text);
        Assert.Contains("本工具记录 2 个", text); Assert.Contains("artifacts", text);
        Assert.DoesNotContain("释放指令已确认", text);
    }
}
