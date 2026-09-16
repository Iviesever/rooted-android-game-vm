using System.Diagnostics;
using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record MemoryProtectionNotice(string State, string Message, HostMemorySnapshot Host, DateTimeOffset At);

public sealed partial class DebugBroker
{
    private volatile MemoryProtectionNotice? _memoryNotice;

    private async Task MonitorMemoryAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var clock = Stopwatch.StartNew();
        var tracker = new MemoryPressureTracker();
        string? handledSession = null;
        string? observedSession = null;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var host = HostMemory.Read();
                    if (host.AvailableMb >= VmMemoryPolicy.ReserveMb(host.TotalMb) && host.AvailableCommitMb is >= 1024)
                    {
                        tracker.Reset(); handledSession = null;
                        if (_memoryNotice?.State == "warning") _memoryNotice = null;
                        continue;
                    }
                    // Controller monitors startup itself. Do not race start/restore or a storage switch.
                    if (_jobs.Values.Any(j => !j.Completed && j.Command is "start" or "stop" or "checkpoint.restore" or "checkpoint.create" or "checkpoint.recover"))
                    { tracker.Reset(); continue; }
                    string session;
                    lock (_leaseLock)
                    {
                        var owned = _service.Instance.Require(force: true);
                        session = $"{owned.ProcessId}:{owned.StartedAtUtcTicks}";
                    }
                    if (session != observedSession) { tracker.Reset(); observedSession = session; }
                    if (session == handledSession) continue;
                    _memoryNotice = new("warning", $"Windows 可用内存偏低：{host.AvailableMb} MiB。持续严重不足时会保存并停止安卓。", host, DateTimeOffset.UtcNow);
                    if (!tracker.ShouldStop(host, clock.Elapsed)) continue;
                    handledSession = session;
                    _memoryNotice = new("stopping", $"Windows 内存持续不足（可用 {host.AvailableMb} MiB），正在取消调试、保存并停止安卓。", host, DateTimeOffset.UtcNow);
                    await SaveMemoryNoticeAsync(ct);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(90));
                    var reply = await DispatchAsync(DebugRequest.Create("stop", new { expectedSession = session }), deadline.Token);
                    if (reply.Ok && reply.Result is not null)
                    {
                        var jobId = System.Text.Json.JsonSerializer.SerializeToElement(reply.Result, DebugJson.Options).GetProperty("jobId").GetString()!;
                        var job = _jobs[jobId];
                        await (job.Work ?? Task.CompletedTask).WaitAsync(deadline.Token);
                        reply = job.Result ?? DebugReply.Failure(new IOException("停止任务未返回结果。"));
                    }
                    _memoryNotice = new(reply.Ok ? "stopped" : "stop_failed", reply.Ok
                        ? "宿主内存持续不足，已保存并停止安卓。释放内存后可手动重新启动。"
                        : "内存保护未能正常停止安卓：" + reply.Error?.Message, host, DateTimeOffset.UtcNow);
                    await SaveMemoryNoticeAsync(ct);
                }
                catch (DebugException error) when (error.Code is "device_offline" or "instance_mismatch") { tracker.Reset(); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    tracker.Reset();
                    // Do not force-kill on a failed sync or unavailable ownership proof.
                    if (_memoryNotice is { } previous)
                        _memoryNotice = previous with { State = "stop_failed", Message = "内存保护操作未完成：" + error.Message };
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task SaveMemoryNoticeAsync(CancellationToken ct)
    {
        // Only capacity/action evidence. No app data, process environment or credentials.
        try
        {
            var root = new Storage.ProductStorageLocation().ControlRoot;
            Storage.StoragePathPolicy.RejectReparsePoints(root);
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "memory-protection.json");
            var temp = path + ".tmp";
            Storage.StoragePathPolicy.RejectReparsePoints(path);
            Storage.StoragePathPolicy.RejectReparsePoints(temp);
            await File.WriteAllTextAsync(temp, DebugJson.Write(_memoryNotice!), ct);
            File.Move(temp, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unavailable log destination must not block the safety action.
            if (_memoryNotice is { } notice) _memoryNotice = notice with { Message = notice.Message + "（本机记录写入失败）" };
        }
    }
}
