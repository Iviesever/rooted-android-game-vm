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
    private static readonly HashSet<string> Readers = ["status", "memory.snapshot", "capabilities", "schema", "runtime.inspect", "screen", "preview", "preview.benchmark", "frames.sample", "apps", "app.observe", "metrics", "checkpoint.list", "files.list", "logs", "record", "trace", "licenses"];
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token); ct = linked.Token;
        RestoreJobs();
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
        finally
        {
            linked.Cancel();
            foreach (var job in _jobs.Values.Where(job => !job.Completed)) job.Cancel.Cancel();
            try { await Task.WhenAll(_jobs.Values.Select(job => job.Work)).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { /* Unfinished journals remain interrupted on recovery, never succeeded. */ }
            await memoryWatch;
        }
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
                    await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(reply with { Result = frame.Metadata }, DebugJson.Options), requestTimeout.Token);
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
        var previous = DebugOperation.Current.Value;
        var requestId = request.RequestId ?? Guid.NewGuid().ToString("N");
        if (!System.Text.RegularExpressions.Regex.IsMatch(requestId, "^[a-zA-Z0-9_.:-]{1,128}$"))
            return DebugReply.Failure(new ArgumentException("requestId须为1–128个字母、数字、下划线、点、冒号或连字符。"));
        request = request with { RequestId = requestId };
        var operation = new DebugOperation(requestId, null,
            Path.Combine(_service.Paths.ProductRoot, "debug-runs", "requests", Guid.NewGuid().ToString("N")))
            { Stage = request.Command ?? "request", CaptureTools = request.Command is not ("status" or "preview" or "runtime.inspect" or "memory.snapshot") };
        DebugOperation.Current.Value = operation;
        try
        {
            if (string.IsNullOrWhiteSpace(request.Command)) throw new ArgumentException("缺少command。");
            var reply = await DispatchCoreAsync(request, ct);
            return reply.JobId is not null ? reply with { RequestId = requestId } : operation.Complete(reply);
        }
        catch (Exception error) { return operation.Complete(DebugReply.Failure(error)); }
        finally { DebugOperation.Current.Value = previous; }
    }

    private async Task<DebugReply> DispatchCoreAsync(DebugRequest request, CancellationToken ct)
    {
        if (request.SchemaVersion != 1) return DebugReply.Failure(new ArgumentException("未知协议版本。"));
        if (request.Command.Length > 128 || !DebugCommandCatalog.Commands.Any(command => command.Name == request.Command))
            return DebugReply.Failure(new ArgumentException("未知命令或命令名超长。"));
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
            var quiet = await DispatchAsync(new("quiesce"), ct);
            if (!quiet.Ok) return quiet;
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
            var id = Guid.NewGuid().ToString("N");
            created = new DebugJob(request.Command, request.RequestId!, id, DateTimeOffset.UtcNow,
                Path.Combine(_service.Paths.ProductRoot, "debug-runs", "requests", id), JobJournalDirectory);
            created.Operation.Changed = created.Persist;
            created.Operation.EnsureDirectory();
            File.WriteAllText(Path.Combine(created.Operation.DirectoryPath, "request.json"), DebugJson.Write(request));
            created.Persist(); _jobs[created.Id] = created;
        }
        var resultDirectory = Path.Combine(_service.Paths.ProductRoot, "debug-runs", "job-results");
        _ = Task.Run(async () =>
        {
            DebugOperation.Current.Value = created.Operation;
            AndroidDebugService.Progress.Value = value =>
            { created.Progress = created.Operation.CaptureProgress(value); created.Persist(); };
            using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(created.Cancel.Token);
            try
            {
                var seconds = Math.Clamp(request.Number("timeoutSeconds", request.Command is "shell" or "root-shell" ? 120 : 7200), 1, 7200);
                timeLimit.CancelAfter(TimeSpan.FromSeconds(seconds));
                var result = await InvokeAsync(request, timeLimit.Token);
                if (timeLimit.IsCancellationRequested && !created.Cancel.IsCancellationRequested)
                    result = result with { Ok = false, Error = (result.Error ?? new("timeout", "调试任务超时。")) with
                        { Code = "timeout", Message = "调试任务超时；已结束本次工具调用，清理结果见阶段及工具记录。 " + result.Error?.Message } };
                result = created.Operation.Complete(result);
                created.Stored = await StoredJobResult.WriteAsync(resultDirectory, created.Id, result);
            }
            catch (Exception error) { created.Failure = created.Operation.Complete(DebugReply.Failure(error)); }
            finally
            {
                created.Completed = true;
                try { created.Persist(); }
                catch (Exception error) { created.Failure = created.Operation.Complete(DebugReply.Failure(error)); }
                AndroidDebugService.Progress.Value = null; DebugOperation.Current.Value = null;
                created.Completion.TrySetResult();
            }
        }, CancellationToken.None);
        return new(true, new { jobId = created.Id, requestId = request.RequestId, status = "queued", inputLeaseSeconds = request.Command == "input" ? (int?)5 : null },
            RequestId: request.RequestId, JobId: created.Id, Stage: "queued", ArtifactDirectory: created.Operation.DirectoryPath);
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
                if (DebugOperation.Current.Value is { } operation) operation.Stage = request.Command;
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
    private string JobJournalDirectory => Path.Combine(_service.Paths.ProductRoot, "debug-runs", "jobs");
    private void RestoreJobs()
    {
        foreach (var record in DebugJobJournalStore.Load(JobJournalDirectory))
        {
            var job = new DebugJob(record.Command, record.RequestId, record.JobId, record.Created, record.ArtifactDirectory, JobJournalDirectory);
            job.Operation.Stage = record.Stage; job.Operation.Session = record.Session; job.Operation.Pid = record.Pid;
            job.Progress = record.Progress; job.Completed = true;
            job.OwnerPid = record.BrokerPid; job.OwnerStartedAtUtcTicks = record.BrokerStartedAtUtcTicks;
            if (record.Completed && record.ResultPath is not null && record.ResultBytes is not null && record.Ok is not null)
                job.Stored = new(record.ResultPath, record.ResultBytes.Value, record.Ok.Value, record.Error,
                    job.Operation.Complete(new(record.Ok.Value, Error: record.Error)));
            else job.Failure = record.Completed && record.Error is not null
                ? job.Operation.Complete(new(false, Error: record.Error))
                : DebugJobJournalStore.Interrupted(record, Path.Combine(JobJournalDirectory, record.JobId + ".json"));
            _jobs[job.Id] = job;
            job.Completion.TrySetResult();
            if (!record.Completed) job.Persist();
        }
    }
    private sealed class DebugJob(string command, string requestId, string id, DateTimeOffset created, string directory, string journalDirectory)
    {
        public string Id { get; } = id; public string Command { get; } = command;
        public DateTimeOffset Created { get; } = created;
        public DebugOperation Operation { get; } = new(requestId, id, directory);
        private readonly object _journalLock = new();
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Work => Completion.Task;
        public int OwnerPid { get; set; } = Environment.ProcessId;
        public long OwnerStartedAtUtcTicks { get; set; } = ReadBrokerStart();
        private static long ReadBrokerStart() { using var process = Process.GetCurrentProcess(); return process.StartTime.ToUniversalTime().Ticks; }
        public volatile bool Completed; public StoredJobResult? Stored; public DebugReply? Failure;
        public DebugReply? Result => Failure ?? Stored?.Read();
        public object? Progress;
        public void Persist()
        {
            lock (_journalLock)
            {
                DebugJobJournalStore.Save(journalDirectory, new(Id, Operation.RequestId, Command, Created, Completed,
                    Failure?.Stage ?? Stored?.Error?.Stage ?? Operation.Stage, Operation.Session, Operation.Pid, Operation.DirectoryPath,
                    Stored?.Path, Stored?.Bytes, Stored?.Ok, Failure?.Error ?? Stored?.Error,
                    Progress is null ? null : JsonSerializer.SerializeToElement(Progress, DebugJson.Options), OwnerPid, OwnerStartedAtUtcTicks));
            }
        }
        public object Snapshot(bool includeResult = true) => new
        {
            jobId = Id,
            requestId = Operation.RequestId,
            command = Command,
            created = Created,
            completed = Completed,
            status = !Completed ? Cancel.IsCancellationRequested ? "cancelling" : Operation.Stage == "queued" ? "queued" : "running" :
                Failure?.Terminal ?? (Stored?.Ok == true ? "succeeded" : Stored?.Error?.Code switch { "cancelled" => "cancelled", "timeout" => "timed_out", "interrupted" => "interrupted", _ => "failed" }),
            stage = Failure?.Stage ?? Stored?.Error?.Stage ?? Operation.Stage,
            session = Operation.Session,
            pid = Operation.Pid,
            artifactDirectory = Operation.DirectoryPath,
            brokerPid = OwnerPid,
            brokerStartedAtUtcTicks = OwnerStartedAtUtcTicks,
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
        if (!reply.Ok) throw DebugException.FromError(reply.Error!);
        var data = (JsonElement)reply.Result!;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("jobId", out var id)) return data;
        var jobId = id.GetString();
        try
        {
            while (true)
            {
                await Task.Delay(500, ct);
                reply = await SendAsync(DebugRequest.Create("job", new { id = jobId }), ct);
                if (!reply.Ok) throw DebugException.FromError(reply.Error!);
                var job = (JsonElement)reply.Result!;
                progress?.Invoke(job);
                if (!job.GetProperty("completed").GetBoolean()) continue;
                var finished = job.GetProperty("result");
                if (!finished.GetProperty("ok").GetBoolean()) throw DebugException.FromError(finished.GetProperty("error").Deserialize<DebugError>(DebugJson.Options)!);
                return finished.GetProperty("result");
            }
        }
        catch (OperationCanceledException)
        {
            try { await CancelAndWaitAsync(jobId!, CancellationToken.None); }
            catch (Exception cleanup) { throw new OperationCanceledException("已请求取消，但未确认清理终态；请查询任务 " + jobId + "。 " + cleanup.Message, cleanup, ct); }
            throw;
        }
    }
    public async Task<DebugReply> CancelAndWaitAsync(string jobId, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var reply = await SendAsync(DebugRequest.Create("cancel", new { id = jobId }), deadline.Token);
        while (reply.Ok && reply.Result is JsonElement job && !job.GetProperty("completed").GetBoolean())
        {
            await Task.Delay(100, deadline.Token);
            reply = await SendAsync(DebugRequest.Create("job", new { id = jobId }), deadline.Token);
        }
        if (!reply.Ok) throw DebugException.FromError(reply.Error!);
        if (reply.Result is JsonElement interrupted && interrupted.GetProperty("status").GetString() == "interrupted")
            throw new DebugException("cancellation_unconfirmed", "原协调进程中断，未确认取消后的清理终态；请检查任务 " + jobId + " 的恢复记录。");
        return reply;
    }
    public async Task<DebugReply> SendAsync(DebugRequest request, CancellationToken ct = default, bool autoStart = true)
    {
        request = request with { RequestId = request.RequestId ?? Guid.NewGuid().ToString("N") };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            using var pipe = await ConnectAsync(timeout.Token, autoStart);
            await DebugBroker.WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(DebugJson.Write(request)), timeout.Token);
            return JsonSerializer.Deserialize<DebugReply>(await DebugBroker.ReadFrameAsync(pipe, timeout.Token), DebugJson.Options) ?? throw new IOException("协调进程返回空响应。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("协调进程请求超时；尚未确认原请求终态，请凭请求/任务编号查询。"); }
    }
    public async Task<PreviewFrame> ReadPreviewAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var pipe = await ConnectAsync(timeout.Token, true);
        await DebugBroker.WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(DebugJson.Write(new DebugRequest("preview"))), timeout.Token);
        var reply = JsonSerializer.Deserialize<DebugReply>(await DebugBroker.ReadFrameAsync(pipe, timeout.Token, 1024 * 1024), DebugJson.Options)!;
        if (!reply.Ok) throw DebugException.FromError(reply.Error!);
        var metadata = ((JsonElement)reply.Result!).Deserialize<PreviewMetadata>(DebugJson.Options)!;
        if (!metadata.BinaryPayload || metadata.Encoding != "png" || metadata.PayloadBytes is < 24 or > 8 * 1024 * 1024)
            throw new IOException("预览元数据无效。");
        var bytes = await DebugBroker.ReadFrameAsync(pipe, timeout.Token, metadata.PayloadBytes);
        if (bytes.Length != metadata.PayloadBytes)
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
