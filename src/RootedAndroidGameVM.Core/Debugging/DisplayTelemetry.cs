using System.Globalization;
using System.Text.RegularExpressions;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record DisplayTelemetry(int? Width, int? Height, double? ActiveRefreshRate,
    double? CompositorRenderRate, double[] SupportedRefreshRates, string? Renderer)
{
    public bool ConfirmsRequestedRate(int requested) => ActiveRefreshRate is double rate && Math.Abs(rate - requested) < 0.5;

    public static DisplayTelemetry Parse(string displayDump, string surfaceDump)
    {
        var activeLine = displayDump.Split('\n').FirstOrDefault(line => line.Contains("mActiveSfDisplayMode=", StringComparison.Ordinal)) ?? "";
        var deviceLine = displayDump.Split('\n').FirstOrDefault(line => line.Contains("DisplayDeviceInfo{", StringComparison.Ordinal) && line.Contains("FLAG_ALLOWED_TO_BE_DEFAULT_DISPLAY", StringComparison.Ordinal))
            ?? displayDump.Split('\n').FirstOrDefault(line => line.Contains("DisplayDeviceInfo{", StringComparison.Ordinal)) ?? "";
        double? Number(string source, string expression)
        {
            var match = Regex.Match(source, expression, RegexOptions.CultureInvariant);
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number : null;
        }
        var width = Number(activeLine, @"\bwidth=(\d+)");
        var height = Number(activeLine, @"\bheight=(\d+)");
        var active = Number(activeLine, @"\bvsyncRate=([\d.]+)") ?? Number(activeLine, @"\bpeakRefreshRate=([\d.]+)");
        var rates = Regex.Matches(deviceLine, @"\bfps=([\d.]+)")
            .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
        var renderer = surfaceDump.Split('\n').FirstOrDefault(line => line.StartsWith("GLES:", StringComparison.Ordinal))?.Trim();
        return new((int?)width, (int?)height, active, Number(deviceLine, @"\brenderFrameRate ([\d.]+)"), rates, renderer);
    }
}
