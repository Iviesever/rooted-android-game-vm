using System.Globalization;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record FrameTimingSummary(int Frames, double? ObservedFps, double? P50Ms, double? P95Ms, double? P99Ms, double? MaxMs, double? ObservedSpanSeconds = null)
{
    public static string NormalizeLayerName(string line)
    {
        line = line.Trim();
        if (!line.StartsWith("RequestedLayerState{", StringComparison.Ordinal)) return line;
        var end = line.IndexOf(" parentId=", StringComparison.Ordinal);
        return end > 20 ? line["RequestedLayerState{".Length..end] : line["RequestedLayerState{".Length..].TrimEnd('}');
    }
    public static long[] ParsePresentedTimes(string dump) => dump.Split('\n')
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(columns => columns.Length == 3)
        .Select(columns => long.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var time) ? time : 0)
        .Where(time => time > 0 && time < long.MaxValue).Distinct().Order().ToArray();

    public static FrameTimingSummary From(IEnumerable<long> presentedTimes)
    {
        var times = presentedTimes.Distinct().Order().ToArray();
        if (times.Length < 2) return new(times.Length, null, null, null, null, null);
        var intervals = times.Zip(times.Skip(1), (first, second) => (second - first) / 1_000_000d).ToArray();
        var sorted = intervals.Order().ToArray();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
        return new(times.Length, (times.Length - 1) * 1_000_000_000d / (times[^1] - times[0]), Percentile(.5), Percentile(.95), Percentile(.99), sorted[^1], (times[^1] - times[0]) / 1_000_000_000d);
    }
}
