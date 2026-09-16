using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;
using System.Runtime.Versioning;

namespace RootedAndroidGameVM.Core.Debugging;

[SupportedOSPlatform("windows")]
public sealed class OwnedInstance(InstallPaths paths, AndroidVmOptions options, IEmulatorProcessCatalog? catalog = null) : IDisposable
{
    private readonly IEmulatorProcessCatalog _catalog = catalog ?? new WindowsEmulatorProcessCatalog();
    private readonly object _cacheLock = new();
    private HostProcessIdentity? _cached;
    private System.Diagnostics.Process? _process;
    private long _validatedAt;
    public string AvdDirectory => Path.Combine(paths.AvdHome, options.AvdName + ".avd");
    public IReadOnlyList<HostProcessIdentity> Processes()
    {
        if (!StorageOwnership.IsOwned(paths.ProductRoot)) throw new DebugException("instance_mismatch", "资源目录不属于本产品。");
        StoragePathPolicy.RejectReparsePoints(AvdDirectory);
        return new[] { "qemu-system-x86_64.exe", "qemu-system-x86_64-headless.exe" }
            .SelectMany(name => _catalog.FindByExecutable(Path.Combine(paths.SdkRoot, "emulator", "qemu", "windows-x86_64", name))).ToArray();
    }
    public HostProcessIdentity Require(bool force = false)
    {
        lock (_cacheLock)
        {
            if (!force && _cached is not null && _process is not null && !_process.HasExited &&
                System.Diagnostics.Stopwatch.GetElapsedTime(_validatedAt) < TimeSpan.FromSeconds(1)) return _cached;
            var verified = Verify();
            if (_cached?.ProcessId != verified.ProcessId || _cached?.StartedAtUtcTicks != verified.StartedAtUtcTicks)
            {
                _process?.Dispose();
                _process = System.Diagnostics.Process.GetProcessById(verified.ProcessId);
            }
            _cached = verified; _validatedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            return verified;
        }
    }
    private HostProcessIdentity Verify()
    {
        var matches = Processes().Where(p => Matches(p, AvdDirectory, options)).ToArray();
        if (matches.Length != 1) throw new DebugException(matches.Length == 0 ? "device_offline" : "instance_mismatch", "无法唯一确认产品虚拟机进程；操作已停止。");
        var target = matches[0];
        foreach (var port in new[] { options.Port, options.Port + 1 })
        {
            var owners = _catalog.GetListenerOwners(port);
            if (owners.Count == 0 || owners.Any(id => id != target.ProcessId))
                throw new DebugException("instance_mismatch", "模拟器端口不属于已验证的产品进程。");
        }
        return target;
    }
    public void RequireStopped()
    {
        Dispose();
        var parents = _catalog.FindByExecutable(Path.Combine(paths.SdkRoot, "emulator", "emulator.exe"));
        if (Processes().Concat(parents).Any(p => string.Equals(p.AvdDirectory, AvdDirectory, StringComparison.OrdinalIgnoreCase)))
            throw new DebugException("instance_busy", "请先完全关闭当前虚拟机。");
    }
    public void RequirePortsFree(int grpcPort)
    {
        RequireStopped();
        if (new[] { options.Port, options.Port + 1, grpcPort }.Any(p => _catalog.GetListenerOwners(p).Count != 0))
            throw new DebugException("port_conflict", "产品启动端口已被占用；不会停止或接管其他设备。");
    }
    public static bool Matches(HostProcessIdentity p, string directory, AndroidVmOptions options) =>
        p.AvdName == options.AvdName && p.ConsolePort == options.Port &&
        string.Equals(p.AvdDirectory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase);
    public async Task WaitStoppedAsync(CancellationToken ct)
    {
        var processes = Processes().Concat(_catalog.FindByExecutable(Path.Combine(paths.SdkRoot, "emulator", "emulator.exe")));
        foreach (var p in processes.Where(p => Matches(p, AvdDirectory, options))) await _catalog.WaitForExitAsync(p, ct);
        RequireStopped();
    }
    public void Dispose()
    {
        lock (_cacheLock) { _process?.Dispose(); _process = null; _cached = null; }
    }
}
