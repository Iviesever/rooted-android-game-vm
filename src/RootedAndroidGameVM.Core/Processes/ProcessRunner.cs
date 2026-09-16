using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace RootedAndroidGameVM.Core.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    public const int DefaultMaxOutputCharacters = 8 * 1024 * 1024;
    public async Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        CancellationToken cancellationToken = default)
        => await RunRequestAsync(new ProcessRequest(spec), cancellationToken).ConfigureAwait(false);

    public async Task<ProcessResult> RunRequestAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxOutputCharacters is < 0 or > DefaultMaxOutputCharacters)
            throw new ArgumentOutOfRangeException(nameof(request.MaxOutputCharacters));
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = ProcessStartInfoFactory.CreateRequest(request),
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start '{request.Spec.FileName}'.");
        }

        using var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ExceptionDispatchInfo? failure = null;
        void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Already exited. */ }
        }
        using var registration = cancellationToken.Register(Kill);
        async Task<T> Guard<T>(Task<T> work)
        {
            try { return await work.ConfigureAwait(false); }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(error), null);
                Kill(); ioCancellation.Cancel(); throw;
            }
        }
        async Task<bool> WriteInputAsync()
        {
            if (request.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), ioCancellation.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            return true;
        }
        // Read both pipes while feeding stdin. Any failed pipe must stop the producer,
        // including a child waiting forever for more input after overflowing stderr.
        var stdout = Guard(ReadBoundedTextAsync(process.StandardOutput, request.MaxOutputCharacters, ioCancellation.Token));
        var stderr = Guard(ReadBoundedTextAsync(process.StandardError, request.MaxOutputCharacters, ioCancellation.Token));
        var stdin = Guard(WriteInputAsync());
        try
        {
            try { await Task.WhenAll(stdout, stderr, stdin, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false); }
            catch { cancellationToken.ThrowIfCancellationRequested(); failure?.Throw(); throw; }
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        finally { Kill(); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
    }

    public static async Task<string> ReadBoundedTextAsync(TextReader reader, int limit, CancellationToken ct = default)
    {
        if (limit is < 0 or > DefaultMaxOutputCharacters) throw new ArgumentOutOfRangeException(nameof(limit));
        const int blockSize = 4096;
        var blocks = new List<(char[] Buffer, int Count)>();
        char[]? current = null;
        var length = 0;
        try
        {
            while (true)
            {
                current = ArrayPool<char>.Shared.Rent(blockSize);
                var filled = 0;
                while (filled < blockSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var count = await reader.ReadAsync(current.AsMemory(filled, blockSize - filled), ct).ConfigureAwait(false);
                    if (count == 0) break;
                    if (count > limit - length) throw new ProcessOutputLimitException(limit);
                    filled += count; length += count;
                }
                blocks.Add((current, filled)); current = null;
                if (filled < blockSize) break;
            }
            ct.ThrowIfCancellationRequested();
            return string.Create(length, blocks, (destination, source) =>
            {
                var offset = 0;
                foreach (var block in source) { block.Buffer.AsSpan(0, block.Count).CopyTo(destination[offset..]); offset += block.Count; }
            });
        }
        finally
        {
            if (current is not null) ArrayPool<char>.Shared.Return(current, clearArray: true);
            foreach (var block in blocks) ArrayPool<char>.Shared.Return(block.Buffer, clearArray: true);
        }
    }
}

public sealed class ProcessOutputLimitException(int limit) : IOException($"工具输出超过每路 {limit} 字符上限；已停止工具，未返回截断结果。");
