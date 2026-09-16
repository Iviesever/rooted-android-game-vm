using System.Text.Json;
using System.Text.RegularExpressions;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    private static readonly Regex DeviceOverlay = new(@"\Acom\.android\.(?:internal|systemui)\.emulation\.pixel_[a-z0-9_]+\z", RegexOptions.CultureInvariant);
    private async Task<string[]> ReadDeviceOverlaysAsync(CancellationToken ct)
    {
        var listing = await ShellAsync("cmd overlay list --user 0", false, ct);
        return listing.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("[x] ", StringComparison.Ordinal))
            .Select(line => line[4..].Trim()).Where(name => DeviceOverlay.IsMatch(name)).ToArray();
    }

    public async Task<object> ApplyDesktopAppearanceAsync(bool enabled, CancellationToken ct, bool settleStartup = false)
    {
        var active = await ReadDeviceOverlaysAsync(ct);
        var backup = Path.Combine(Paths.ProductRoot, "runtime-settings-backups", "desktop-overlays.json");
        StoragePathPolicy.RejectReparsePoints(backup);
        if (enabled && active.Length > 0 && !File.Exists(backup))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await File.WriteAllTextAsync(backup, DebugJson.Write(new { packages = active }), ct);
        }
        var previous = Array.Empty<string>();
        if (!enabled && File.Exists(backup))
        {
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(backup, ct));
            previous = saved.RootElement.GetProperty("packages").EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (previous.Any(name => !DeviceOverlay.IsMatch(name))) throw new IOException("设备外观备份内容无效。");
        }
        if (enabled || previous.Length > 0)
        {
            foreach (var name in active) await ShellAsync("cmd overlay disable --user 0 " + name, false, ct);
            foreach (var name in previous) await ShellAsync("cmd overlay enable --user 0 " + name, false, ct);
        }
        // The emulator can select its device overlays after sys.boot_completed.
        // At startup, observe a bounded quiet interval before letting callers launch an app.
        var changed = active.ToHashSet(StringComparer.Ordinal);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pressure = new Android.MemoryPressureTracker();
        var quietSince = clock.Elapsed;
        while (enabled)
        {
            await Task.Delay(settleStartup ? 2000 : 300, ct);
            if (settleStartup && pressure.ShouldStop(HostMemory.Read(), clock.Elapsed))
                throw new Android.HostMemoryInsufficientException("启动后的显示配置期间宿主内存持续不足，已中止启动。");
            var remaining = await ReadDeviceOverlaysAsync(ct);
            if (remaining.Length == 0)
            {
                if (!settleStartup || clock.Elapsed - quietSince >= TimeSpan.FromSeconds(12)) break;
            }
            else
            {
                quietSince = clock.Elapsed;
                if (!File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    await File.WriteAllTextAsync(backup, DebugJson.Write(new { packages = remaining }), ct);
                }
                foreach (var name in remaining)
                { changed.Add(name); await ShellAsync("cmd overlay disable --user 0 " + name, false, ct); }
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(40)) throw new DebugException("display_not_ready", "设备外观配置仍在变化，安卓已运行但桌面显示未通过核对。");
        }
        var result = new { desktopDisplay = enabled, disabled = changed.ToArray(), restored = previous, backup, settledSeconds = clock.Elapsed.TotalSeconds };
        var audit = Path.Combine(Paths.ProductRoot, "runtime-settings-backups", "desktop-last-application.json");
        Directory.CreateDirectory(Path.GetDirectoryName(audit)!);
        StoragePathPolicy.RejectReparsePoints(audit);
        await File.WriteAllTextAsync(audit, DebugJson.Write(result), ct);
        return result;
    }
}
