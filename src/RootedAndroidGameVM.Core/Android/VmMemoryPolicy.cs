using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Android;

public sealed record MemoryAdmission(bool Allowed, int GuestMb, long EstimatedVmMb, long ReserveMb,
    long RequiredAvailableMb, long AvailableMb, long RequiredCommitMb, long? AvailableCommitMb, string Message);

public sealed class HostMemoryInsufficientException(string message) : InvalidOperationException(message);

public static class VmMemoryPolicy
{
    // Existing same-AVD samples: resident overhead about 0.9 GiB at 3 GiB guest;
    // private commit overhead about 2.2–2.4 GiB. Budget the two resources separately.
    // These margins are admission planning, not enforced process limits or peak guarantees.
    public const int ResidentOverheadMb = 1536;
    public const int CommitOverheadMb = 2560;
    public const int MinimumStartAvailableMb = 2560;
    public static long ReserveMb(long totalMb) => Math.Clamp(totalMb / 8, 2048, 4096);
    public static long StopThresholdMb(long totalMb) => Math.Clamp(totalMb / 12, 1024, 2048);

    public static MemoryAdmission Assess(int guestMb, HostMemorySnapshot host, int startAvailableMb = 0, bool lowRam = false)
    {
        if (guestMb < (lowRam ? 768 : 1536) || guestMb > 8192 || host.TotalMb <= 0 || host.AvailableMb < 0)
            throw new ArgumentException("无效的内存配置或宿主内存读数。");
        // The pinned API-35 emulator raises normal phone RAM requests below 2560 MiB.
        var plannedGuestMb = lowRam ? guestMb : Math.Max(guestMb, 2560);
        var estimate = (long)plannedGuestMb + ResidentOverheadMb;
        var reserve = ReserveMb(host.TotalMb);
        if (startAvailableMb != 0 && (startAvailableMb < MinimumStartAvailableMb || startAvailableMb > host.TotalMb))
            throw new ArgumentException($"启动物理余量门槛须为 0（自动）或至少 {MinimumStartAvailableMb} MiB，且不超过宿主物理内存。");
        var required = startAvailableMb == 0 ? estimate + reserve : startAvailableMb;
        var commit = (long)plannedGuestMb + CommitOverheadMb + 1024;
        var allowed = plannedGuestMb <= host.TotalMb / 2 && host.AvailableMb >= required && host.AvailableCommitMb >= commit;
        var message = startAvailableMb != 0
            ? $"{(allowed ? "启动检查通过" : "暂不启动")}：自定义物理余量门槛 {required} MiB，当前 {host.AvailableMb} MiB；提交余量需 {commit} MiB，当前 {host.AvailableCommitMb?.ToString() ?? "无法确认"} MiB。安卓配额不得超过宿主物理内存一半。此门槛不代表实际占用，运行期保护保持独立。"
            : allowed
            ? $"启动检查通过：预估安卓 {plannedGuestMb} MiB，预估额外驻留内存 {ResidentOverheadMb} MiB，另留宿主 {reserve} MiB。实际占用仍需监测。"
            : $"暂不启动：宿主可用内存 {host.AvailableMb} MiB，本配置至少需要 {required} MiB（预估安卓 {plannedGuestMb} + 额外驻留预估 {ResidentOverheadMb} + 系统余量 {reserve}）。" +
              $"可用提交空间 {(host.AvailableCommitMb?.ToString() ?? "无法确认")} MiB，需至少 {commit} MiB。请关闭不需要的程序，或降低安卓内存后重试；不会自动关闭其他程序。";
        if (plannedGuestMb != guestMb) message += $" 模拟器预计将请求的 {guestMb} MiB 提高至至少 {plannedGuestMb} MiB；以上容量估算已计入此调整。";
        return new(allowed, guestMb, estimate, reserve, required, host.AvailableMb, commit, host.AvailableCommitMb, message);
    }

    public static void RequireStart(int guestMb, HostMemorySnapshot host, int startAvailableMb = 0, bool lowRam = false)
    {
        var admission = Assess(guestMb, host, startAvailableMb, lowRam);
        if (!admission.Allowed) throw new HostMemoryInsufficientException(admission.Message);
    }
}

public sealed class MemoryPressureTracker
{
    private TimeSpan? _lowSince;
    public void Reset() => _lowSince = null;
    public bool ShouldStop(HostMemorySnapshot host, TimeSpan monotonicNow)
    {
        if (host.AvailableMb < 512 || host.AvailableCommitMb is < 256) return true;
        if (host.AvailableMb >= VmMemoryPolicy.StopThresholdMb(host.TotalMb) && host.AvailableCommitMb is not < 512)
        { Reset(); return false; }
        _lowSince ??= monotonicNow;
        return monotonicNow - _lowSince.Value >= TimeSpan.FromSeconds(6);
    }
}
