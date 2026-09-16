using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record ProcessMemoryRow(string Role, int Pid, long StartedAtUtcTicks, string Executable,
    long WorkingSetBytes, long PrivateCommitBytes, long LifetimePeakWorkingSetBytes, long LifetimePeakPagedBytes);
public sealed record ProcessMemorySnapshot(DateTimeOffset At, double CollectionMs, double InventoryAgeMs, HostMemorySnapshot Host,
    IReadOnlyList<ProcessMemoryRow> Processes, IReadOnlyList<string> Errors, object Observer, string Note)
{
    public long ObservedWorkingSetSumBytes => Processes.Sum(p => p.WorkingSetBytes);
    public long ObservedPrivateCommitSumBytes => Processes.Sum(p => p.PrivateCommitBytes);
    public bool SampleHasErrors => Errors.Count != 0;
}

[SupportedOSPlatform("windows")]
public static class ProcessMemory
{
    public static bool BelongsToVm(HostProcessIdentity identity, InstallPaths paths, AndroidVmOptions options) =>
        OwnedInstance.Matches(identity, Path.Combine(paths.AvdHome, options.AvdName + ".avd"), options);

    public static ProcessMemorySnapshot Read(InstallPaths paths, IEnumerable<string>? programDirectories = null)
        => new ProcessMemorySampler(paths, programDirectories).Read();
}

[SupportedOSPlatform("windows")]
public sealed class ProcessMemorySampler(InstallPaths paths, IEnumerable<string>? programDirectories = null)
{
    private readonly List<(HostProcessIdentity Identity, string Role)> _identities = [];
    private readonly List<string> _discoveryErrors = [];
    private long _inventoryAt;
    public void RefreshInventory() => _inventoryAt = 0;
    public ProcessMemorySnapshot Read()
    {
        var clock = Stopwatch.StartNew();
        var host = HostMemory.Read();
        var rows = new Dictionary<int, ProcessMemoryRow>();
        var errors = new List<string>();
        var options = AndroidVmOptions.ForPaths(paths);
        var catalog = new WindowsEmulatorProcessCatalog();
        if (!StorageOwnership.IsOwned(paths.ProductRoot)) throw new DebugException("instance_mismatch", "资源目录不属于本产品。");
        StoragePathPolicy.RejectReparsePoints(paths.AvdHome);

        void ReadIdentity(HostProcessIdentity identity, string role)
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                process.Refresh();
                if (!string.Equals(process.MainModule?.FileName, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                    process.StartTime.ToUniversalTime().Ticks / 10 != identity.StartedAtUtcTicks / 10)
                    throw new IOException("PID 身份已变化");
                rows[process.Id] = new(role, process.Id, process.StartTime.ToUniversalTime().Ticks, identity.ExecutablePath,
                    process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64, process.PeakPagedMemorySize64);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or IOException)
            { errors.Add($"{role}:{identity.ProcessId}: {error.GetType().Name}: {error.Message}"); }
        }
        if (_inventoryAt == 0 || Stopwatch.GetElapsedTime(_inventoryAt).TotalSeconds >= 5)
        {
            _identities.Clear(); _discoveryErrors.Clear();
            var targets = new Dictionary<string, (string Role, bool Vm)>(StringComparer.OrdinalIgnoreCase);
            targets[Path.Combine(paths.SdkRoot, "emulator", "qemu", "windows-x86_64", "qemu-system-x86_64.exe")] = ("qemu", true);
            targets[Path.Combine(paths.SdkRoot, "emulator", "qemu", "windows-x86_64", "qemu-system-x86_64-headless.exe")] = ("qemu", true);
            targets[Path.Combine(paths.SdkRoot, "emulator", "emulator.exe")] = ("emulator", true);
            targets[Path.Combine(paths.SdkRoot, "platform-tools", "adb.exe")] = ("adb-shared", false);
            foreach (var helper in new[] { "crashpad_handler.exe", "netsimd.exe", "emulator-crash-service.exe" })
                targets[Path.Combine(paths.SdkRoot, "emulator", helper)] = ("emulator-helper-shared", false);
            foreach (var directory in (programDirectories ?? [AppContext.BaseDirectory]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                targets[Path.GetFullPath(Path.Combine(directory, "RootedAndroidGameVM.Cli.exe"))] = ("cli-or-broker", false);
                targets[Path.GetFullPath(Path.Combine(directory, "RootedAndroidGameVM.exe"))] = ("launcher", false);
                targets[Path.GetFullPath(Path.Combine(directory, "RootedAndroidGameVM.Setup.exe"))] = ("setup", false);
            }
            try
            {
                foreach (var identity in catalog.FindByExecutables(targets.Keys))
                {
                    var target = targets[Path.GetFullPath(identity.ExecutablePath)];
                    if (!target.Vm || ProcessMemory.BelongsToVm(identity, paths, options)) _identities.Add((identity, target.Role));
                }
            }
            catch (Exception error) when (error is IOException or Win32Exception or System.Runtime.InteropServices.COMException)
            { _discoveryErrors.Add($"inventory: {error.GetType().Name}: {error.Message}"); }
            _inventoryAt = Stopwatch.GetTimestamp();
        }
        errors.AddRange(_discoveryErrors);
        foreach (var (identity, role) in _identities) ReadIdentity(identity, role);
        var inventoryAgeMs = Stopwatch.GetElapsedTime(_inventoryAt).TotalMilliseconds;
        if (errors.Count > 0) RefreshInventory();
        var gc = GC.GetGCMemoryInfo();
        using var observer = Process.GetCurrentProcess();
        return new(DateTimeOffset.UtcNow, clock.Elapsed.TotalMilliseconds, inventoryAgeMs, host, rows.Values.ToArray(), errors,
            new { pid = observer.Id, managedLiveEstimateBytes = GC.GetTotalMemory(false), lastGcHeapBytes = gc.HeapSizeBytes,
                lastGcFragmentedBytes = gc.FragmentedBytes, allocatedBytes = GC.GetTotalAllocatedBytes(false),
                workingSetBytes = observer.WorkingSet64, privateCommitBytes = observer.PrivateMemorySize64 },
            "WS 求和包含共享页的重复计数，仅为观测到的进程工作集之和；PrivateCommit 不是物理内存。生命周期峰值不是本阶段峰值；同 SDK 的 ADB/辅助进程保守计入，可能被其他设备共享。guest PSS 不可再加到 QEMU。清单最多缓存 5 秒，每次重验 PID/启动时间/路径，短命进程可能漏采；缺失/退出见 errors。observer 为采样进程，不计入产品总和。");
    }
}
