using System.Text.Json;

namespace RootedAndroidGameVM.Core.Debugging;

public static class DebugWireReply
{
    public const int MaximumBytes = 16 * 1024 * 1024;

    // Most replies retain their existing shape. A quick command can still produce more
    // than one pipe frame (e.g. large diagnostics); retain that result instead of closing
    // the pipe as if the caller had disconnected. Reuse the existing result-file contract.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<byte[]> SerializeAsync(DebugReply reply, string resultDirectory, CancellationToken ct)
    {
        try
        {
            using var buffer = new LimitedBuffer();
            await JsonSerializer.SerializeAsync(buffer, reply, DebugJson.Options, ct);
            return buffer.ToArray();
        }
        catch (ReplyLimitException)
        {
            ct.ThrowIfCancellationRequested();
            var stored = await StoredJobResult.WriteAsync(resultDirectory, Guid.NewGuid().ToString("N"), reply);
            return JsonSerializer.SerializeToUtf8Bytes(stored.Reference(), DebugJson.Options);
        }
    }

    private sealed class ReplyLimitException : IOException;
    private sealed class LimitedBuffer : MemoryStream
    {
        private void Check(int count) { if (count > MaximumBytes - Position) throw new ReplyLimitException(); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
    }
}
