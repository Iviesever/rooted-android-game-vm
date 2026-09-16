using System.Diagnostics;
using RootedAndroidGameVM.Core.Debugging;

internal static class BufferBenchmark
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        foreach (var size in new[] { 4096, 4 * 1024 * 1024, 32 * 1024 * 1024 })
        {
            foreach (var mode in new[] { "old-memory-stream", "pooled-blocks", "stream-to-file" })
            {
                // Warm the exact workload, including pool capacity. Report warm steady-state allocations.
                await Run(mode, size, directory);
                for (var repeat = 0; repeat < 3; repeat++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
                    var bytes = await Run(mode, size, directory);
                    results.Add(new { mode, size, repeat, bytes, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, ms = watch.Elapsed.TotalMilliseconds });
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "buffers.json"), DebugJson.Write(new
        {
            note = "合成相同零数据；每组先预热池。GC 仅用于基准隔离，不进入产品。allocatedBytes 是分配量，不是 VM RAM 节省。",
            results
        }));
        var old = new List<DebugReply>();
        GC.Collect(); var before = GC.GetTotalMemory(true);
        for (var i = 0; i < 24; i++) old.Add(new(true, new { stdout = new string('x', 1024 * 1024) }));
        var oldLive = GC.GetTotalMemory(true) - before;
        GC.KeepAlive(old); old.Clear();
        GC.Collect(); before = GC.GetTotalMemory(true);
        var stored = new List<StoredJobResult>();
        for (var i = 0; i < 24; i++) stored.Add(await StoredJobResult.WriteAsync(Path.Combine(directory, "stored"), Guid.NewGuid().ToString("N"), new(true, new { stdout = new string('x', 1024 * 1024) })));
        var newLive = GC.GetTotalMemory(true) - before;
        GC.KeepAlive(stored);
        await File.WriteAllTextAsync(Path.Combine(directory, "retention.json"), DebugJson.Write(new { jobs = 24, outputCharactersPerJob = 1024 * 1024, oldRetainedManagedBytes = oldLive, newRetainedManagedBytes = newLive, diskBytes = stored.Sum(s => s.Bytes), note = "合成存活对象基准；完整结果仍在磁盘。不是 QEMU 或整机占用变化。" }));
        Console.WriteLine("Buffer and result retention benchmarks saved to " + Path.GetFullPath(directory));
    }
    private static async Task<long> Run(string mode, int size, string directory)
    {
        using var source = new ZeroStream(size);
        if (mode == "stream-to-file")
        {
            var path = Path.Combine(directory, "stream-probe.bin");
            await using (var file = File.Create(path)) await BinaryProcess.CopyBoundedAsync(source, file, size, default);
            var length = new FileInfo(path).Length; File.Delete(path); return length;
        }
        var bytes = mode == "pooled-blocks" ? await BinaryProcess.ReadBoundedAsync(source, size, default) : await OldRead(source, size);
        if (bytes.Length != size || bytes.Any(b => b != 0)) throw new IOException("输出不一致。");
        return bytes.Length;
    }
    private static async Task<byte[]> OldRead(Stream source, int limit)
    {
        using var target = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await source.ReadAsync(buffer)) > 0)
        {
            if (target.Length + count > limit) throw new IOException("limit");
            await target.WriteAsync(buffer.AsMemory(0, count));
        }
        return target.ToArray();
    }
    private sealed class ZeroStream(int length) : Stream
    {
        private int _remaining = length;
        private readonly int _length = length;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); var count = Math.Min(buffer.Length, _remaining); buffer.Span[..count].Clear(); _remaining -= count; return ValueTask.FromResult(count); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => _length; public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).Result;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
