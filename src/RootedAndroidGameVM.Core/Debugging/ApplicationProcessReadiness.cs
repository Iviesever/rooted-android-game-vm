namespace RootedAndroidGameVM.Core.Debugging;

public static class ApplicationProcessReadiness
{
    public static async Task<string> WaitAsync(Func<CancellationToken, Task<string>> probe,
        TimeSpan timeout, CancellationToken ct, TimeSpan? interval = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var delay = interval ?? TimeSpan.FromMilliseconds(200);
        if (delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var pid = (await probe(deadline.Token)).Trim();
                if (pid.Length != 0) return pid;
                await Task.Delay(delay, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new DebugException("app_not_running", "启动指令已发送，但限定时间内没有观察到应用进程；请检查应用退出或兼容性日志。"); }
    }
}
