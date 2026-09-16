using System.Buffers;
using System.Diagnostics;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Debugging;

public static class BinaryProcess
{
    private const int BlockSize = 16 * 1024;
    public static Task<byte[]> RunAsync(ProcessSpec spec, int limit, CancellationToken ct) =>
        RunCoreAsync(spec, (stream, token) => ReadBoundedAsync(stream, limit, token), ct);

    public static async Task<string> RunTextAsync(ProcessSpec spec, int limit, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await RunCoreAsync(spec, (stream, token) => ReadBoundedAsync(stream, limit, token), ct, textEvidence: true));

    public static async Task<long> RunToFileAsync(ProcessSpec spec, string path, long limit, CancellationToken ct)
    {
        var temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            long bytes;
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockSize, true))
                bytes = await RunCoreAsync(spec, (stream, token) => CopyBoundedAsync(stream, file, limit, token), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            return bytes;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<T> RunCoreAsync<T>(ProcessSpec spec, Func<Stream, CancellationToken, Task<T>> read, CancellationToken ct, bool textEvidence = false)
    {
        ct.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var ticks = Stopwatch.GetTimestamp();
        var operation = DebugOperation.Current.Value;
        var evidence = textEvidence ? operation?.NewToolPath() : null;
        using var process = Process.Start(ProcessStartInfoFactory.Create(spec)) ?? throw new IOException("无法启动工具。");
        void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
        using var registration = ct.Register(Kill);
        async Task<V> Guard<V>(Task<V> task) { try { return await task; } catch { Kill(); throw; } }
        // Either pipe failing must stop the producer, including a full stderr pipe.
        // Text commands drain both bounded pipes after cancellation kills the owned tree,
        // retaining output produced before interruption. Binary paths keep their old contract.
        var readToken = textEvidence ? CancellationToken.None : ct;
        var stderr = Guard(ReadBoundedAsync(process.StandardError.BaseStream, 1024 * 1024, readToken));
        var stdout = Guard(read(process.StandardOutput.BaseStream, readToken));
        string? failure = null;
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(ct));
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                var detail = System.Text.Encoding.UTF8.GetString(await stderr);
                var code = detail.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? "permission_denied" :
                    detail.Contains("device offline", StringComparison.OrdinalIgnoreCase) || detail.Contains("device not found", StringComparison.OrdinalIgnoreCase) ? "device_offline" :
                    detail.Contains("No space left", StringComparison.OrdinalIgnoreCase) ? "disk_full" : "tool_failed";
                throw new DebugException(code, detail.Length == 0 ? "工具返回非零退出码：" + process.ExitCode : detail);
            }
            return await stdout;
        }
        catch (Exception error)
        {
            failure = error.GetType().Name;
            if (evidence is not null) error.Data["toolEvidencePath"] = evidence;
            throw;
        }
        finally
        {
            Kill(); await process.WaitForExitAsync(CancellationToken.None);
            if (evidence is not null)
            {
                var stdoutPath = Path.ChangeExtension(evidence, ".stdout.txt");
                var stderrPath = Path.ChangeExtension(evidence, ".stderr.txt");
                if (stdout.IsCompletedSuccessfully && stdout.Result is byte[] output) await File.WriteAllBytesAsync(stdoutPath, output);
                if (stderr.IsCompletedSuccessfully) await File.WriteAllBytesAsync(stderrPath, stderr.Result);
                await File.WriteAllTextAsync(evidence, DebugJson.Write(new
                {
                    operation!.RequestId, operation.JobId, operation.Session, stage = operation.Stage,
                    tool = Path.GetFileName(spec.FileName), pid = process.Id, startedAt = started,
                    elapsedMs = Stopwatch.GetElapsedTime(ticks).TotalMilliseconds, exitCode = process.ExitCode,
                    cancelled = ct.IsCancellationRequested, failure, reaped = process.HasExited,
                    stdoutComplete = stdout.IsCompletedSuccessfully, stderrComplete = stderr.IsCompletedSuccessfully,
                    stdoutPath = File.Exists(stdoutPath) ? stdoutPath : null, stderrPath = File.Exists(stderrPath) ? stderrPath : null
                }));
            }
        }
    }

    public static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        long total = 0;
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, BlockSize), ct)) > 0)
            {
                if (count > limit - total) throw LimitExceeded();
                await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                total += count;
            }
            return total;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var blocks = new List<(byte[] Buffer, int Count)>();
        byte[]? current = null;
        var length = 0;
        try
        {
            while (true)
            {
                current = ArrayPool<byte>.Shared.Rent(BlockSize);
                var filled = 0;
                while (filled < BlockSize)
                {
                    var count = await source.ReadAsync(current.AsMemory(filled, BlockSize - filled), ct);
                    if (count == 0) break;
                    if (count > limit - length) throw LimitExceeded();
                    filled += count; length += count;
                }
                blocks.Add((current, filled)); current = null;
                if (filled < BlockSize) break;
            }
            var result = new byte[length]; var offset = 0;
            foreach (var block in blocks) { block.Buffer.AsSpan(0, block.Count).CopyTo(result.AsSpan(offset)); offset += block.Count; }
            return result;
        }
        finally
        {
            if (current is not null) ArrayPool<byte>.Shared.Return(current, clearArray: true);
            foreach (var block in blocks) ArrayPool<byte>.Shared.Return(block.Buffer, clearArray: true);
        }
    }
    private static DebugException LimitExceeded() => new("output_limit", "输出超过安全上限；本次结果已截断并拒绝使用。");
}
