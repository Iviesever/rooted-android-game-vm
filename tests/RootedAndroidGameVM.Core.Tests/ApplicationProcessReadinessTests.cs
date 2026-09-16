using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ApplicationProcessReadinessTests
{
    [Fact]
    public async Task A_delayed_pid_is_observed_without_sending_a_second_launch()
    {
        var replies = new Queue<string>(["", "\r\n", " 2468\r\n"]);
        var calls = 0;
        var pid = await ApplicationProcessReadiness.WaitAsync(_ => { calls++; return Task.FromResult(replies.Dequeue()); },
            TimeSpan.FromSeconds(2), default, TimeSpan.FromMilliseconds(1));
        Assert.Equal("2468", pid); Assert.Equal(3, calls);
    }
    [Fact]
    public async Task Missing_pid_has_a_bounded_and_specific_failure()
    {
        var error = await Assert.ThrowsAsync<DebugException>(() => ApplicationProcessReadiness.WaitAsync(
            _ => Task.FromResult(""), TimeSpan.FromMilliseconds(30), default, TimeSpan.FromMilliseconds(1)));
        Assert.Equal("app_not_running", error.Code);
    }
    [Fact]
    public async Task External_cancellation_is_not_misreported_as_app_failure()
    {
        using var cancel = new CancellationTokenSource();
        var work = ApplicationProcessReadiness.WaitAsync(async token => { cancel.Cancel(); await Task.Delay(1000, token); return ""; },
            TimeSpan.FromSeconds(10), cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
    }
}
