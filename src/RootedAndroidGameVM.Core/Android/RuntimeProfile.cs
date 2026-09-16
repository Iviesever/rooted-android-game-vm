using System.Globalization;

namespace RootedAndroidGameVM.Core.Android;

public sealed record RuntimeProfile(
    string Renderer,
    int Width,
    int Height,
    int Density,
    int RefreshRate,
    int MemoryMb,
    int CpuCores,
    bool Vulkan = false,
    string Orientation = "landscape",
    bool DesktopDisplay = true)
{
    public static RuntimeProfile Recommended { get; } = new("host", 1920, 1080, 240, 120, 3072, 4);

    public RuntimeProfile Validate(long hostMemoryMb = long.MaxValue, int hostLogicalCores = 128)
    {
        if (Renderer is not ("host" or "software" or "swiftshader" or "swiftshader_indirect"))
            throw new ArgumentException("未知图形后端。");
        if (Width is < 480 or > 3840 || Height is < 320 or > 3840 || (long)Width * Height > 8_294_400)
            throw new ArgumentException("显示尺寸必须在支持范围内，且不超过 4K 像素量。");
        if (Density is < 120 or > 640 || RefreshRate is not (60 or 90 or 120))
            throw new ArgumentException("密度范围为 120–640；刷新率可选 60、90 或 120 Hz。");
        if (MemoryMb is < 1536 or > 8192 || MemoryMb > hostMemoryMb / 2)
            throw new ArgumentException("安卓内存须为 1536–8192 MiB，并为宿主保留至少一半物理内存。");
        if (CpuCores is < 2 or > 16 || CpuCores > Math.Max(2, hostLogicalCores / 2))
            throw new ArgumentException("处理器数量超限，须为宿主保留足够核心。");
        if (Orientation is not ("portrait" or "landscape")) throw new ArgumentException("未知初始方向。");
        return this;
    }

    public IReadOnlyDictionary<string, string> ToAvdSettings() => new Dictionary<string, string>
    {
        ["hw.gpu.enabled"] = "yes",
        ["hw.gpu.mode"] = Renderer,
        ["hw.lcd.width"] = Width.ToString(CultureInfo.InvariantCulture),
        ["hw.lcd.height"] = Height.ToString(CultureInfo.InvariantCulture),
        ["hw.lcd.density"] = Density.ToString(CultureInfo.InvariantCulture),
        ["hw.lcd.vsync"] = RefreshRate.ToString(CultureInfo.InvariantCulture),
        ["hw.ramSize"] = MemoryMb.ToString(CultureInfo.InvariantCulture),
        ["hw.cpu.ncore"] = CpuCores.ToString(CultureInfo.InvariantCulture),
        ["hw.initialOrientation"] = Orientation,
        ["rgvm.vulkan"] = Vulkan ? "yes" : "no",
        ["rgvm.desktopDisplay"] = DesktopDisplay ? "yes" : "no",
        ["rgvm.runtimeProfile"] = "1"
    };

    public static RuntimeProfile FromAvdSettings(IEnumerable<string> lines)
    {
        var values = lines.Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2)).GroupBy(parts => parts[0].Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.Ordinal);
        int Number(string key, int fallback) => values.TryGetValue(key, out var text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;
        return new RuntimeProfile(values.GetValueOrDefault("hw.gpu.mode", "host"),
            Number("hw.lcd.width", 1920), Number("hw.lcd.height", 1080), Number("hw.lcd.density", 240),
            Number("hw.lcd.vsync", 60), Number("hw.ramSize", Recommended.MemoryMb), Number("hw.cpu.ncore", 4),
            values.GetValueOrDefault("rgvm.vulkan") == "yes", values.GetValueOrDefault("hw.initialOrientation", "landscape"),
            values.GetValueOrDefault("rgvm.desktopDisplay") == "yes");
    }
}
