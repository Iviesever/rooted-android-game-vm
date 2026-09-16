using System.Diagnostics;

namespace RootedAndroidGameVM.Core.Debugging;

public static class InputClock
{
    public static long ResolveStart(long? requested, long now, long frequency)
    {
        if (requested is null) return now;
        if (requested < 0 || requested > now + frequency * 120)
            throw new ArgumentException("startAtQpc须为本机QPC时间戳，且不超过未来120秒。");
        if (requested < now - frequency / 20)
            throw new DebugException("input_schedule_missed", "指定的输入起点已经过去；拒绝补发过期序列，请重新观察并安排未来时刻。", "preparing_input");
        return requested.Value;
    }
    public static double MillisecondsSince(long start) => (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
}
public sealed record TouchDispatchTiming(long SentTimestamp, long AcknowledgedTimestamp);
