using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Storage;

public sealed class AndroidStorageRuntime
{
    private readonly IEmulatorProcessCatalog _catalog;
    private readonly IProcessRunner _runner;
    private readonly Func<AndroidSdkLayout, AndroidVmOptions, Func<int, CancellationToken, Task>?, IAndroidVmLifecycle> _factory;
    private readonly Func<StorageVerificationBinding, CancellationToken, Task>? _recordBinding;
    private readonly Func<StorageVerificationBinding?>? _readBinding;
    private readonly Dictionary<string, StorageVerificationBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);

    public AndroidStorageRuntime(IEmulatorProcessCatalog? catalog = null, IProcessRunner? runner = null,
        Func<AndroidSdkLayout, AndroidVmOptions, Func<int, CancellationToken, Task>?, IAndroidVmLifecycle>? factory = null,
        Func<StorageVerificationBinding, CancellationToken, Task>? recordBinding = null,
        Func<StorageVerificationBinding?>? readBinding = null)
    {
        if (catalog is null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("资源迁移需要 Windows。");
            catalog = new WindowsEmulatorProcessCatalog();
        }
        _catalog = catalog;
        _runner = runner ?? new ProcessRunner();
        _factory = factory ?? ((layout, options, guard) => new AndroidVmController(layout, options,
            requireFreshStart: guard is not null, validateStartedProcess: guard));
        _recordBinding = recordBinding;
        _readBinding = readBinding;
    }

    public async Task StopAsync(InstallPaths paths, CancellationToken cancellationToken)
    {
        var layout = AndroidSdkLayout.FromRoot(paths.SdkRoot);
        var expected = AndroidVmOptions.ForPaths(paths);
        var avdDirectory = Path.Combine(paths.AvdHome, expected.AvdName + ".avd");
        var processes = _catalog.FindByExecutable(QemuPath(paths));
        var stops = new List<(HostProcessIdentity Process, IAndroidVmLifecycle Controller)>();
        var binding = _bindings.GetValueOrDefault(paths.ProductRoot) ?? _readBinding?.Invoke();
        foreach (var process in processes)
        {
            if (process.AvdName != expected.AvdName || !SamePath(process.AvdDirectory, avdDirectory) ||
                process.ConsolePort is not { } port || port < 5554 || port > 5682 || port % 2 != 0)
                throw new InvalidOperationException("无法确认资源目录中的模拟器数据位置。请先关闭该模拟器，再重试迁移。");
            if (binding is { QemuProcessId: > 0 } && SamePath(binding.AvdDirectory, avdDirectory) &&
                (binding.QemuProcessId != process.ProcessId || binding.StartedAtUtcTicks != process.StartedAtUtcTicks))
                throw new IOException("验证进程身份已变化，请先关闭相关模拟器后恢复迁移。");
            AssertEndpoints(process, port);
            var options = expected with { Port = port, Serial = $"emulator-{port}" };
            var controller = _factory(layout, options, null);
            if (await controller.GetStatusAsync(cancellationToken).ConfigureAwait(false) != VmStatus.Running)
                throw new InvalidOperationException("模拟器仍在运行，但 ADB 身份尚未确认。请关闭后重试。");
            stops.Add((process, controller));
        }
        foreach (var (process, controller) in stops)
        {
            AssertCurrentIdentity(process);
            AssertEndpoints(process, process.ConsolePort!.Value);
            await controller.StopAsync(cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            await _catalog.WaitForExitAsync(process, deadline.Token).ConfigureAwait(false);
        }
        if (_catalog.FindByExecutable(QemuPath(paths)).Count != 0)
            throw new IOException("资源目录仍被模拟器使用，已停止复制或清理。");
        foreach (var process in _catalog.FindByExecutable(layout.AdbPath))
            await _catalog.TerminateAsync(process, cancellationToken).ConfigureAwait(false);
        _bindings.Remove(paths.ProductRoot);
    }

    public async Task VerifyAsync(InstallPaths paths, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(paths.ProductRoot, "install.json"))) return;
        if (_catalog.FindByExecutable(QemuPath(paths)).Count != 0)
            throw new InvalidOperationException("新位置已有模拟器运行，不能代替本次迁移验证。");
        var port = Enumerable.Range(0, 8).Select(index => 5570 + index * 2)
            .FirstOrDefault(candidate => _catalog.GetListenerOwners(candidate).Count == 0 &&
                                         _catalog.GetListenerOwners(candidate + 1).Count == 0);
        if (port == 0) throw new IOException("没有空闲的迁移验证端口，请关闭其他模拟器后重试。");
        var layout = AndroidSdkLayout.FromRoot(paths.SdkRoot);
        var options = AndroidVmOptions.ForPaths(paths) with { Port = port, Serial = $"emulator-{port}", Headless = true };
        var directory = Path.Combine(paths.AvdHome, options.AvdName + ".avd");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        var planned = new StorageVerificationBinding(0, 0, 0, port, directory, QemuPath(paths));
        if (_recordBinding is not null) await _recordBinding(planned, token).ConfigureAwait(false);

        async Task Guard(int starterProcessId, CancellationToken guardToken)
        {
            var candidates = _catalog.FindByExecutable(QemuPath(paths)).Where(process =>
                process.ConsolePort == port && process.AvdName == options.AvdName && SamePath(process.AvdDirectory, directory) &&
                (process.ParentProcessId == starterProcessId || process.ProcessId == starterProcessId)).ToList();
            if (candidates.Count != 1) throw new IOException("验证实例不属于本次目标目录启动，已停止操作。");
            var process = candidates[0];
            AssertEndpoints(process, port);
            var identity = new StorageVerificationBinding(starterProcessId, process.ProcessId, process.StartedAtUtcTicks,
                port, directory, process.ExecutablePath);
            if (_bindings.TryGetValue(paths.ProductRoot, out var previous) && previous != identity)
                throw new IOException("验证进程或端口归属发生变化，已停止操作。");
            _bindings[paths.ProductRoot] = identity;
            if (_recordBinding is not null) await _recordBinding(identity, guardToken).ConfigureAwait(false);
        }

        var controller = _factory(layout, options, Guard);
        await controller.StartAsync(token).ConfigureAwait(false);
        if (!_bindings.TryGetValue(paths.ProductRoot, out var verified))
            throw new IOException("验证启动没有提供进程归属证明。");
        await Guard(verified.StarterProcessId, token).ConfigureAwait(false);
        var diagnostics = await controller.DiagnoseAsync(token).ConfigureAwait(false);
        if (!diagnostics.Contains("Root：正常（uid=0）", StringComparison.Ordinal))
            throw new InvalidOperationException("新位置未通过 Root 验证，原目录仍然保留。" + Environment.NewLine + diagnostics);
        await Guard(verified.StarterProcessId, token).ConfigureAwait(false);
        var result = await _runner.RunAsync(AndroidCommandFactory.RootShell(layout, options,
            "test -d /data/data"), token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("新位置的私有数据目录不可访问，原目录仍然保留。");
    }

    private void AssertCurrentIdentity(HostProcessIdentity expected)
    {
        if (!_catalog.FindByExecutable(expected.ExecutablePath).Contains(expected))
            throw new IOException("进程身份已改变，已停止操作。");
    }

    private void AssertEndpoints(HostProcessIdentity process, int port)
    {
        foreach (var endpoint in new[] { port, port + 1 })
        {
            var owners = _catalog.GetListenerOwners(endpoint);
            if (owners.Count != 1 || !owners.Contains(process.ProcessId))
                throw new IOException("模拟器端口被其他进程占用或归属不明，已停止操作。");
        }
    }

    private static string QemuPath(InstallPaths paths) =>
        Path.Combine(paths.SdkRoot, "emulator", "qemu", "windows-x86_64", "qemu-system-x86_64.exe");

    private static bool SamePath(string? left, string right) => left is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
