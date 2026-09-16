using System.Text.RegularExpressions;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record GuestMemoryObservation(int RequestedMb, int? AllocatedMb, long? GuestVisibleKiB,
    bool AllocationMatchesRequest, string Session, string Note);

public sealed partial class AndroidDebugService
{
    private async Task<GuestMemoryObservation> ReadGuestMemoryAsync(RuntimeProfile profile, CancellationToken ct)
    {
        var identity = Instance.Require(force: true);
        var path = Path.Combine(Instance.AvdDirectory, "hardware-qemu.ini");
        StoragePathPolicy.RejectReparsePoints(path);
        var hardware = File.Exists(path) ? File.ReadAllText(path) : "";
        var guest = await ShellAsync("head -n 3 /proc/meminfo", false, ct);
        var after = Instance.Require(force: true);
        if (identity.ProcessId != after.ProcessId || identity.StartedAtUtcTicks != after.StartedAtUtcTicks)
            throw new DebugException("stale_observation", "读取内存期间虚拟机会话发生变化。");
        var ram = Regex.Match(hardware, @"(?m)^hw\.ramSize\s*=\s*(\d+)\s*$");
        var visible = Regex.Match(guest, @"(?m)^MemTotal:\s*(\d+)\s+kB");
        var allocated = ram.Success && int.TryParse(ram.Groups[1].Value, out var mb) ? (int?)mb : null;
        var visibleKiB = visible.Success && long.TryParse(visible.Groups[1].Value, out var kb) ? (long?)kb : null;
        return new(profile.MemoryMb, allocated, visibleKiB, allocated == profile.MemoryMb,
            $"{identity.ProcessId}:{identity.StartedAtUtcTicks}",
            "Allocated 来自本次 hardware-qemu.ini；guest MemTotal 扣除了内核保留区。两者均不是宿主进程总占用。低内存模式下仍需核验引擎是否调整 RAM/堆设置。");
    }
}
