using System.Diagnostics;
using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    public async Task<object> PreviewBenchmarkAsync(int frames, CancellationToken ct)
    {
        frames = Math.Clamp(frames, 5, 120);
        await PreviewAsync(ct); await PreviewAsync(ct);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var beforePrivate = process.PrivateMemorySize64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var timings = new List<double>(); var totalBytes = 0L; var peakPrivate = beforePrivate;
        for (var index = 0; index < frames; index++)
        {
            var clock = Stopwatch.StartNew();
            var frame = await PreviewAsync(ct);
            timings.Add(clock.Elapsed.TotalMilliseconds); totalBytes += frame.Payload.Length;
            process.Refresh(); peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
            await Task.Delay(250, ct);
        }
        var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
        var sorted = timings.Order().ToArray();
        var result = new
        {
            frames,
            averageMs = timings.Average(),
            medianMs = sorted[sorted.Length / 2],
            p95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
            maxMs = sorted[^1],
            bytesTransferred = totalBytes,
            managedAllocatedBytes = allocated,
            brokerPrivateBefore = beforePrivate,
            brokerPrivatePeak = peakPrivate,
            frameFilesWritten = 0,
            note = "预览采集开销；不是游戏帧率或端到端输入延迟。"
        };
        var directory = NewRecord("preview-benchmark");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), DebugJson.Write(result), ct);
        return new { directory, measurement = result };
    }

    public async Task<object> SampleFrameTimingsAsync(string package, int seconds, CancellationToken ct)
    {
        AndroidPackageName.Parse(package); seconds = Math.Clamp(seconds, 2, 120);
        var directory = NewRecord("frame-timings");
        var layers = (await ShellAsync("dumpsys SurfaceFlinger --list", false, ct, strict: false)).Split('\n')
            .Select(FrameTimingSummary.NormalizeLayerName).Where(line => line.Contains(package, StringComparison.Ordinal) && line.Contains("SurfaceView", StringComparison.Ordinal)).ToArray();
        var layer = layers.FirstOrDefault(line => line.Contains("BLAST", StringComparison.Ordinal)) ?? layers.FirstOrDefault();
        if (layer is null) return new { directory, available = false, reason = "未找到应用 SurfaceView 图层", package };
        await ShellAsync("dumpsys SurfaceFlinger --latency-clear " + Q(layer), false, ct, strict: false);
        var times = new HashSet<long>(); var samples = new List<object>(); var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var dump = await ShellAsync("dumpsys SurfaceFlinger --latency " + Q(layer), false, ct, strict: false);
            var presented = FrameTimingSummary.ParsePresentedTimes(dump);
            foreach (var time in presented) times.Add(time);
            samples.Add(new { elapsedMs = clock.Elapsed.TotalMilliseconds, presentedTimes = presented });
            await Task.Delay(500, ct);
        }
        var summary = FrameTimingSummary.From(times);
        var result = new
        {
            package,
            layer,
            available = summary.ObservedFps is not null,
            summary,
            durationSeconds = clock.Elapsed.TotalSeconds,
            source = "SurfaceFlinger FrameTracker actualPresentTime",
            coverage = "轮询有限历史缓冲，可能缺样；不跨重启推断帧率"
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), DebugJson.Write(result), ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "samples.json"), DebugJson.Write(samples), ct);
        return new { directory, measurement = result };
    }
}
