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
public sealed partial class DebugBroker : IDisposable
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
    private static readonly HashSet<string> Quick = ["status", "memory.snapshot", "capabilities", "schema", "runtime.inspect", "screen", "preview", "apps", "metrics", "checkpoint.list", "files.list", "clipboard", "release", "wake", "key"];
    private static readonly HashSet<string> Readers = ["status", "memory.snapshot", "capabilities", "schema", "runtime.inspect", "screen", "preview", "preview.benchmark", "frames.sample", "apps", "metrics", "checkpoint.list", "files.list", "logs", "record", "trace", "licenses"];
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token); ct = linked.Token;
        _service.MemoryNotice = () => _memoryNotice;
        var memoryWatch = MonitorMemoryAsync(ct);
        using var timer = new Timer(_ =>
        {
            foreach (var job in _jobs.Values)
                if (job.Command == "input" && !job.Completed && DateTimeOffset.UtcNow - job.LastSeen > TimeSpan.FromSeconds(5)) job.Cancel.Cancel();
        }, null, 1000, 1000);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(ct); }
                catch { await pipe.DisposeAsync(); throw; }
                _ = ServeAsync(pipe, ct);
            }
        }
        finally { linked.Cancel(); await memoryWatch; }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); requestTimeout.CancelAfter(TimeSpan.FromMinutes(3));
                var bytes = await ReadFrameAsync(pipe, requestTimeout.Token, 1024 * 1024);
                var request = JsonSerializer.Deserialize<DebugRequest>(bytes, DebugJson.Options) ?? throw new ArgumentException("空请求。");
                var reply = await DispatchAsync(request, requestTimeout.Token);
                if (reply.Result is PreviewFrame frame)
                {
                    await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new DebugReply(true, frame.Metadata), DebugJson.Options), requestTimeout.Token);
                    await WriteFrameAsync(pipe, frame.Payload, requestTimeout.Token);
                }
                else await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(reply, DebugJson.Options), requestTimeout.Token);
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
        if (request.Command == "jobs") return new(true, _jobs.Values.Select(j => j.Snapshot(includeResult: false)).ToArray());
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
            try { _service.Instance.RequireStopped(); }
            catch (Exception error) { return DebugReply.Failure(new DebugException("instance_busy", "请先停止虚拟机，再退出协调进程；运行期间需要保留内存保护。" + error.Message)); }
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
        DebugJob created;
        lock (_jobs)
        {
            foreach (var old in _jobs.Values.Where(j => j.Completed).OrderBy(j => j.Created).Take(Math.Max(0, _jobs.Count - 127)))
                _jobs.TryRemove(old.Id, out _);
            if (_jobs.Values.Count(j => !j.Completed) >= 16) return DebugReply.Failure(new DebugException("busy", "并发任务过多。"));
            created = new DebugJob(request.Command); _jobs[created.Id] = created;
        }
        var resultDirectory = Path.Combine(_service.Paths.ProductRoot, "debug-runs", "job-results");
        created.Work = Task.Run(async () =>
        {
            AndroidDebugService.Progress.Value = value => created.Progress = value;
            using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(created.Cancel.Token);
            try
            {
                var seconds = Math.Clamp(request.Number("timeoutSeconds", request.Command is "shell" or "root-shell" ? 120 : 7200), 1, 7200);
                timeLimit.CancelAfter(TimeSpan.FromSeconds(seconds));
                var result = await InvokeAsync(request, timeLimit.Token);
                if (timeLimit.IsCancellationRequested && !created.Cancel.IsCancellationRequested)
                    result = DebugReply.Failure(new TimeoutException("调试任务超时，已停止并清理所属操作。"));
                created.Stored = await StoredJobResult.WriteAsync(resultDirectory, created.Id, result);
            }
            catch (Exception error) { created.Failure = DebugReply.Failure(error); }
            finally { created.Progress = null; created.Completed = true; AndroidDebugService.Progress.Value = null; }
        }, CancellationToken.None);
        return new(true, new { jobId = created.Id, status = "queued", inputLeaseSeconds = request.Command == "input" ? (int?)5 : null });
    }
    private async Task<DebugReply> InvokeAsync(DebugRequest request, CancellationToken ct)
    {
        var mutate = !Readers.Contains(request.Command);
        var exclusive = request.Command is "start" or "stop" or "checkpoint.create" or "checkpoint.restore" or "checkpoint.recover" or "runtime.configure";
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
                                if (current != _service.Paths || request.Command == "start") { _service.Dispose(); _service = new(current) { MemoryNotice = () => _memoryNotice }; }
                            }
                            _active++; _exclusive = exclusive; entered = true;
                        }
                    }
                    if (!entered) await Task.Delay(100, ct);
                }
                if (request.Command == "start") _memoryNotice = null;
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
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct, int limit = 16 * 1024 * 1024)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct); var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > limit) throw new IOException("消息超出协议上限。");
        var buffer = new byte[length]; await stream.ReadExactlyAsync(buffer, ct); return buffer;
    }
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken ct)
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
        public volatile bool Completed; public StoredJobResult? Stored; public DebugReply? Failure;
        public DebugReply? Result => Failure ?? Stored?.Read();
        public object? Progress;
        public object Snapshot(bool includeResult = true) => new
        {
            jobId = Id,
            command = Command,
            created = Created,
            completed = Completed,
            status = !Completed ? Cancel.IsCancellationRequested ? "cancelling" : "running" : Failure is null && Stored?.Ok == true ? "succeeded" : "failed",
            progress = Progress,
            result = includeResult ? Result : Failure ?? Stored?.Reference(),
            resultPath = Stored?.Path
        };
    }
}

[SupportedOSPlatform("windows")]
public sealed class DebugClient
{
    public async Task<JsonElement> ExecuteAndWaitAsync(DebugRequest request, CancellationToken ct = default, Action<JsonElement>? progress = null)
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
                progress?.Invoke(job);
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
        using var pipe = await ConnectAsync(ct, autoStart);
        await DebugBroker.WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(DebugJson.Write(request)), ct);
        return JsonSerializer.Deserialize<DebugReply>(await DebugBroker.ReadFrameAsync(pipe, ct), DebugJson.Options) ?? throw new IOException("协调进程返回空响应。");
    }
    public async Task<PreviewFrame> ReadPreviewAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var pipe = await ConnectAsync(timeout.Token, true);
        await DebugBroker.WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(DebugJson.Write(new DebugRequest("preview"))), timeout.Token);
        var reply = JsonSerializer.Deserialize<DebugReply>(await DebugBroker.ReadFrameAsync(pipe, timeout.Token), DebugJson.Options)!;
        if (!reply.Ok) throw new DebugException(reply.Error!.Code, reply.Error.Message);
        var metadata = ((JsonElement)reply.Result!).Deserialize<PreviewMetadata>(DebugJson.Options)!;
        var bytes = await DebugBroker.ReadFrameAsync(pipe, timeout.Token);
        if (!metadata.BinaryPayload || metadata.Encoding != "png" || bytes.Length != metadata.PayloadBytes || bytes.Length > 8 * 1024 * 1024)
            throw new IOException("预览帧不完整。");
        var size = AndroidDebugService.PngSize(bytes);
        if (size.Width != metadata.Width || size.Height != metadata.Height) throw new IOException("预览尺寸不一致。");
        return new(metadata, bytes);
    }
    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct, bool autoStart)
    {
        var pipe = new NamedPipeClientStream(".", DebugBroker.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            try { await pipe.ConnectAsync(300, ct); }
            catch (TimeoutException) when (autoStart)
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "RootedAndroidGameVM.Cli.exe");
                if (!File.Exists(executable)) throw new FileNotFoundException("缺少 CLI 协调进程，请修复安装。", executable);
                // ShellExecute detaches all inherited console/pipe handles. Hide this console helper window.
                Process.Start(new ProcessStartInfo(executable) { Arguments = "--broker", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
                await pipe.ConnectAsync(15000, ct);
            }
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
}
