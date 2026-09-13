using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

[SupportedOSPlatform("windows")]
public sealed class DebugBroker : IDisposable
{
    public static string PipeName => "RootedAndroidGameVM.Debug.v1." + WindowsIdentity.GetCurrent().User!.Value;
    private AndroidDebugService _service = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, DebugJob> _jobs = new();
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly object _leaseLock = new();
    private int _active;
    private bool _exclusive;
    private StorageOperationLease? _storageLease;
    private static readonly HashSet<string> Quick = ["status", "capabilities", "screen", "apps", "metrics", "checkpoint.list", "files.list", "clipboard", "release", "wake", "key"];
    private static readonly HashSet<string> Readers = ["status", "capabilities", "screen", "apps", "metrics", "checkpoint.list", "files.list", "logs", "record", "trace", "licenses"];
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token); ct = linked.Token;
        using var timer = new Timer(_ =>
        {
            foreach (var job in _jobs.Values)
                if (job.Command == "input" && !job.Completed && DateTimeOffset.UtcNow - job.LastSeen > TimeSpan.FromSeconds(5)) job.Cancel.Cancel();
        }, null, 1000, 1000);
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(ct); }
            catch { await pipe.DisposeAsync(); throw; }
            _ = ServeAsync(pipe, ct);
        }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); requestTimeout.CancelAfter(TimeSpan.FromMinutes(3));
                var bytes = await ReadFrameAsync(pipe, requestTimeout.Token);
                var request = JsonSerializer.Deserialize<DebugRequest>(bytes, DebugJson.Options) ?? throw new ArgumentException("空请求。");
                var reply = await DispatchAsync(request, requestTimeout.Token);
                await WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(DebugJson.Write(reply)), requestTimeout.Token);
            }
            catch (IOException) { /* Client disconnected. Finite input leases release its contacts. */ }
            catch (Exception e)
            {
                try { await WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(DebugJson.Write(DebugReply.Failure(e))), ct); } catch { }
            }
        }
    }
    public async Task<DebugReply> DispatchAsync(DebugRequest request, CancellationToken ct)
    {
        if (request.SchemaVersion != 1) return DebugReply.Failure(new ArgumentException("未知协议版本。"));
        if (request.Command == "jobs") return new(true, _jobs.Values.Select(j => j.Snapshot()).ToArray());
        if (request.Command is "job" or "cancel")
        {
            if (!_jobs.TryGetValue(request.Text("id"), out var job)) return DebugReply.Failure(new ArgumentException("任务不存在。"));
            job.LastSeen = DateTimeOffset.UtcNow;
            if (request.Command == "cancel") job.Cancel.Cancel();
            return new(true, job.Snapshot());
        }
        if (request.Command == "quiesce")
        {
            foreach (var job in _jobs.Values) job.Cancel.Cancel();
            var work = _jobs.Values.Select(j => j.Work ?? Task.CompletedTask).ToArray();
            await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(30), ct);
            await _service.ReleaseAsync(); return new(true, new { idle = true });
        }
        if (request.Command == "shutdown")
        {
            await DispatchAsync(new("quiesce"), ct);
            _shutdown.CancelAfter(500);
            return new(true, new { shuttingDown = true });
        }
        if (request.Command is "stop" or "checkpoint.create" or "checkpoint.restore" or "checkpoint.recover" or "release")
        {
            var conflicts = _jobs.Values.Where(j => !j.Completed && (request.Command != "release" || j.Command == "input")).ToArray();
            foreach (var job in conflicts) job.Cancel.Cancel();
            await Task.WhenAll(conflicts.Select(j => j.Work ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        if (Quick.Contains(request.Command)) return await InvokeAsync(request, ct);
        // Keep bounded task metadata. Artifacts on disk are not removed by pruning.
        foreach (var old in _jobs.Values.Where(j => j.Completed).OrderBy(j => j.Created).Take(Math.Max(0, _jobs.Count - 127)))
            if (_jobs.TryRemove(old.Id, out var removed)) removed.Cancel.Dispose();
        if (_jobs.Count >= 256) return DebugReply.Failure(new DebugException("busy", "并发任务过多。"));
        var created = new DebugJob(request.Command); _jobs[created.Id] = created;
        created.Work = Task.Run(async () =>
        {
            AndroidDebugService.Progress.Value = value => created.Progress = value;
            using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(created.Cancel.Token);
            var seconds = Math.Clamp(request.Number("timeoutSeconds", request.Command is "shell" or "root-shell" ? 120 : 7200), 1, 7200);
            timeLimit.CancelAfter(TimeSpan.FromSeconds(seconds));
            created.Result = await InvokeAsync(request, timeLimit.Token);
            if (timeLimit.IsCancellationRequested && !created.Cancel.IsCancellationRequested)
                created.Result = DebugReply.Failure(new TimeoutException("调试任务超时，已停止并清理所属操作。"));
            created.Completed = true;
        }, CancellationToken.None);
        return new(true, new { jobId = created.Id, status = "queued", inputLeaseSeconds = request.Command == "input" ? (int?)5 : null });
    }
    private async Task<DebugReply> InvokeAsync(DebugRequest request, CancellationToken ct)
    {
        var mutate = !Readers.Contains(request.Command);
        var exclusive = request.Command is "start" or "stop" or "checkpoint.create" or "checkpoint.restore" or "checkpoint.recover";
        var entered = false;
        try
        {
            if (mutate) await _mutations.WaitAsync(ct);
            try
            {
                while (!entered)
                {
                    ct.ThrowIfCancellationRequested();
                    lock (_leaseLock)
                    {
                        if (!_exclusive && (!exclusive || _active == 0))
                        {
                            if (_active == 0)
                            {
                                _storageLease = StorageOperationLease.Acquire();
                                var current = Setup.InstallPaths.CreateDefault();
                                if (current != _service.Paths || request.Command == "start") { _service.Dispose(); _service = new(current); }
                            }
                            _active++; _exclusive = exclusive; entered = true;
                        }
                    }
                    if (!entered) await Task.Delay(100, ct);
                }
                return new(true, await _service.ExecuteAsync(request, ct));
            }
            finally
            {
                if (entered) lock (_leaseLock)
                {
                    _active--; if (exclusive) _exclusive = false;
                    if (_active == 0) { _storageLease?.Dispose(); _storageLease = null; }
                }
                if (mutate) _mutations.Release();
            }
        }
        catch (Exception e) { return DebugReply.Failure(e); }
    }
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct); var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 16 * 1024 * 1024) throw new IOException("消息超出协议上限。");
        var buffer = new byte[length]; await stream.ReadExactlyAsync(buffer, ct); return buffer;
    }
    public static async Task WriteFrameAsync(Stream stream, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new IOException("响应超出协议上限。");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public void Dispose() { foreach (var job in _jobs.Values) job.Cancel.Cancel(); _service.Dispose(); _storageLease?.Dispose(); _mutations.Dispose(); }
    private sealed class DebugJob(string command)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N"); public string Command { get; } = command;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancel { get; } = new(); public Task? Work { get; set; }
        public volatile bool Completed; public DebugReply? Result;
        public object? Progress;
        public object Snapshot() => new
        {
            jobId = Id,
            command = Command,
            created = Created,
            completed = Completed,
            status = !Completed ? Cancel.IsCancellationRequested ? "cancelling" : "running" : Result?.Ok == true ? "succeeded" : "failed",
            progress = Progress,
            result = Result
        };
    }
}

[SupportedOSPlatform("windows")]
public sealed class DebugClient
{
    public async Task<JsonElement> ExecuteAndWaitAsync(DebugRequest request, CancellationToken ct = default)
    {
        var reply = await SendAsync(request, ct);
        if (!reply.Ok) throw new DebugException(reply.Error!.Code, reply.Error.Message);
        var data = (JsonElement)reply.Result!;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("jobId", out var id)) return data;
        var jobId = id.GetString();
        try
        {
            while (true)
            {
                await Task.Delay(500, ct);
                reply = await SendAsync(DebugRequest.Create("job", new { id = jobId }), ct);
                if (!reply.Ok) throw new DebugException(reply.Error!.Code, reply.Error.Message);
                var job = (JsonElement)reply.Result!;
                if (!job.GetProperty("completed").GetBoolean()) continue;
                var finished = job.GetProperty("result");
                if (!finished.GetProperty("ok").GetBoolean()) throw new DebugException(finished.GetProperty("error").GetProperty("code").GetString()!, finished.GetProperty("error").GetProperty("message").GetString()!);
                return finished.GetProperty("result");
            }
        }
        catch (OperationCanceledException) { await SendAsync(DebugRequest.Create("cancel", new { id = jobId }), CancellationToken.None); throw; }
    }
    public async Task<DebugReply> SendAsync(DebugRequest request, CancellationToken ct = default, bool autoStart = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(3)); ct = timeout.Token;
        using var pipe = new NamedPipeClientStream(".", DebugBroker.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(300, ct); }
        catch (TimeoutException) when (autoStart)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "RootedAndroidGameVM.Cli.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("缺少 CLI 协调进程，请修复安装。", executable);
            // ShellExecute detaches all inherited console/pipe handles. Hide this console helper window.
            Process.Start(new ProcessStartInfo(executable) { Arguments = "--broker", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
            await pipe.ConnectAsync(15000, ct);
        }
        await DebugBroker.WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(DebugJson.Write(request)), ct);
        return JsonSerializer.Deserialize<DebugReply>(await DebugBroker.ReadFrameAsync(pipe, ct), DebugJson.Options) ?? throw new IOException("协调进程返回空响应。");
    }
}
