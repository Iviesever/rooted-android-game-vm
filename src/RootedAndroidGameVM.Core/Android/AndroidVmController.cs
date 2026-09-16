using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Ui;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Android;

public sealed class AndroidVmController : IAndroidVmLifecycle
{
    private readonly AndroidSdkLayout _layout;
    private readonly AndroidVmOptions _options;
    private readonly IProcessRunner _runner;
    private readonly DetachedProcessLauncher _detachedLauncher;
    private readonly AndroidVmStartupPolicy _startupPolicy;
    private DetachedProcessHandle? _activeEmulatorHandle;
    private readonly bool _requireFreshStart;
    private readonly Func<int, CancellationToken, Task>? _validateStartedProcess;
    private readonly Func<HostMemorySnapshot> _readHostMemory;

    public AndroidVmController(
        AndroidSdkLayout? layout = null,
        AndroidVmOptions? options = null,
        IProcessRunner? runner = null,
        DetachedProcessLauncher? detachedLauncher = null,
        AndroidVmStartupPolicy? startupPolicy = null,
        bool requireFreshStart = false,
        Func<int, CancellationToken, Task>? validateStartedProcess = null,
        Func<HostMemorySnapshot>? readHostMemory = null)
    {
        _layout = layout ?? AndroidSdkLayout.Discover();
        _options = options ?? AndroidVmOptions.Default;
        _runner = runner ?? new ProcessRunner();
        _detachedLauncher = detachedLauncher ?? new DetachedProcessLauncher();
        _startupPolicy = startupPolicy ?? AndroidVmStartupPolicy.Default;
        _requireFreshStart = requireFreshStart;
        _validateStartedProcess = validateStartedProcess;
        _readHostMemory = readHostMemory ?? (() => OperatingSystem.IsWindows() ? HostMemory.Read() :
            throw new PlatformNotSupportedException("无法确认 Windows 宿主的可用内存。"));
    }

    public async Task<VmStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_layout.HasRequiredTools)
        {
            return VmStatus.NotInstalled;
        }

        var avds = await _runner.RunRequestAsync(
            AndroidCommandFactory.ListAvds(_layout, _options),
            cancellationToken);
        if (avds.ExitCode != 0 ||
            !avds.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(name => string.Equals(name.Trim(), _options.AvdName, StringComparison.Ordinal)))
        {
            return VmStatus.NotInstalled;
        }

        var deviceState = await _runner.RunAsync(
            AndroidCommandFactory.Adb(_layout, _options, "get-state"),
            cancellationToken);
        if (deviceState.ExitCode != 0 ||
            !string.Equals(deviceState.StandardOutput.Trim(), "device", StringComparison.OrdinalIgnoreCase))
        {
            return VmStatus.Stopped;
        }

        var runningAvd = await _runner.RunAsync(
            AndroidCommandFactory.Adb(_layout, _options, "shell", "getprop", "ro.boot.qemu.avd_name"),
            cancellationToken);
        return runningAvd.ExitCode == 0 &&
               string.Equals(runningAvd.StandardOutput.Trim(), _options.AvdName, StringComparison.Ordinal)
            ? VmStatus.Running
            : VmStatus.Stopped;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (status == VmStatus.NotInstalled)
        {
            throw new InvalidOperationException("Android 虚拟机尚未安装。");
        }

        if (status == VmStatus.Running)
        {
            if (_requireFreshStart)
                throw new InvalidOperationException("验证端口已有模拟器连接，不能用既有实例替代新位置验证。");
            return;
        }

        await ReleaseActiveEmulatorHandleAsync(killIfRunning: true, CancellationToken.None);
        VmMemoryPolicy.RequireStart(_options.MemoryMb, _readHostMemory());

        var diagnosticLogPath = _options.Verbose && !string.IsNullOrWhiteSpace(_options.AvdHome)
            ? Path.Combine(
                _options.AvdHome,
                $"{_options.AvdName}-emulator-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.log")
            : null;
        var emulatorHandle = _detachedLauncher.Start(
            AndroidCommandFactory.StartEmulator(_layout, _options),
            AndroidEmulatorEnvironment.Create(_layout, _options),
            diagnosticLogPath);
        _activeEmulatorHandle = emulatorHandle;
        var emulatorProcess = emulatorHandle.Process;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_startupPolicy.Timeout);
        using var memoryWatch = new CancellationTokenSource();
        Exception? memoryFailure = null;
        var monitor = WatchMemoryAsync();
        async Task WatchMemoryAsync()
        {
            var tracker = new MemoryPressureTracker();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    await Task.Delay(1000, memoryWatch.Token);
                    var snapshot = _readHostMemory();
                    if (!tracker.ShouldStop(snapshot, clock.Elapsed)) continue;
                    memoryFailure = new HostMemoryInsufficientException($"启动期间宿主内存不足（可用 {snapshot.AvailableMb} MiB），已中止本次模拟器启动。请释放内存后重试。");
                    timeout.Cancel(); return;
                }
            }
            catch (OperationCanceledException) when (memoryWatch.IsCancellationRequested) { }
            catch (Exception error) { memoryFailure = error; timeout.Cancel(); }
        }

        try
        {
            var wait = await _runner.RunAsync(
                AndroidCommandFactory.Adb(_layout, _options, "wait-for-device"),
                timeout.Token);
            EnsureSuccess(wait, "等待安卓虚拟机连接");

            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_validateStartedProcess is not null)
                    await _validateStartedProcess(emulatorProcess.Id, timeout.Token);
                var boot = await _runner.RunAsync(
                    AndroidCommandFactory.Adb(_layout, _options, "shell", "getprop", "sys.boot_completed"),
                    timeout.Token);
                if (boot.ExitCode == 0 && boot.StandardOutput.Trim() == "1")
                {
                    var packageService = await _runner.RunAsync(
                        AndroidCommandFactory.CheckPackageService(_layout, _options),
                        timeout.Token);
                    if (!AndroidBootReadiness.IsPackageServiceReady(packageService))
                    {
                        await Task.Delay(_startupPolicy.PollInterval, timeout.Token);
                        continue;
                    }
                    if (_validateStartedProcess is not null)
                        await _validateStartedProcess(emulatorProcess.Id, timeout.Token);
                    await new AndroidInteractiveSessionService(_layout, _options, _runner)
                        .PrepareAsync(timeout.Token);
                    await _runner.RunAsync(
                        AndroidCommandFactory.Adb(_layout, _options, "shell", "settings", "put", "secure",
                            "show_ime_with_hard_keyboard", "0"),
                        timeout.Token);
                    if (!_options.Verbose)
                    {
                        _activeEmulatorHandle = null;
                        await emulatorHandle.DisposeAsync();
                    }
                    return;
                }

                await Task.Delay(_startupPolicy.PollInterval, timeout.Token);
            }
        }
        catch (Exception exception)
        {
            if (memoryFailure is not null)
            {
                // Startup failed: best-effort guest flush before the existing process-tree cleanup.
                using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _runner.RunAsync(AndroidCommandFactory.Adb(_layout, _options, "shell", "sync"), flushTimeout.Token); }
                catch { /* Guest may not have reached ADB readiness. */ }
            }
            var processState = emulatorProcess.HasExited
                ? $"Emulator exited with code {emulatorProcess.ExitCode}."
                : "Emulator was still running when startup timed out.";
            if (!emulatorProcess.HasExited)
            {
                emulatorProcess.Kill(entireProcessTree: true);
                await emulatorProcess.WaitForExitAsync(CancellationToken.None);
            }
            _activeEmulatorHandle = null;
            await DisposeHandlePreservingPrimaryFailureAsync(emulatorHandle);
            var diagnostics = ReadDiagnosticTail(diagnosticLogPath);

            if (memoryFailure is not null) throw memoryFailure;

            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"安卓虚拟机未能在 {_startupPolicy.Timeout.TotalMinutes:0} 分钟内完成启动。" +
                    $"{Environment.NewLine}{processState}{Environment.NewLine}{diagnostics}",
                    exception);
            }
            throw;
        }
        finally { memoryWatch.Cancel(); await monitor; }
    }

    private static string ReadDiagnosticTail(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return "Emulator diagnostic output is unavailable.";
            }
            var lines = File.ReadLines(path).TakeLast(80).ToArray();
            return lines.Length == 0
                ? "Emulator diagnostic output was empty."
                : "Emulator diagnostic tail:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Emulator diagnostic output could not be read: {exception.GetType().Name}.";
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (await GetStatusAsync(cancellationToken) != VmStatus.Running)
        {
            await ReleaseActiveEmulatorHandleAsync(killIfRunning: true, CancellationToken.None);
            return;
        }

        // The emulator shutdown command does not flush Android's delayed filesystem
        // writes. Complete guest sync before closing the process or copying its disks.
        var flush = await _runner.RunAsync(
            AndroidCommandFactory.Adb(_layout, _options, "shell", "sync"), cancellationToken);
        EnsureSuccess(flush, "保存安卓数据；未完成保存时保留运行中的虚拟机");
        var result = await _runner.RunAsync(AndroidCommandFactory.StopEmulator(_layout, _options), cancellationToken);
        EnsureSuccess(result, "停止安卓虚拟机");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await _runner.RunAsync(
                AndroidCommandFactory.Adb(_layout, _options, "get-state"),
                cancellationToken);
            if (state.ExitCode != 0 ||
                !string.Equals(state.StandardOutput.Trim(), "device", StringComparison.OrdinalIgnoreCase))
            {
                await ReleaseActiveEmulatorHandleAsync(killIfRunning: true, cancellationToken);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException("安卓虚拟机未能在一分钟内完全停止。");
    }

    private async Task ReleaseActiveEmulatorHandleAsync(
        bool killIfRunning,
        CancellationToken cancellationToken)
    {
        var handle = _activeEmulatorHandle;
        if (handle is null) return;
        if (!handle.Process.HasExited && killIfRunning)
        {
            handle.Process.Kill(entireProcessTree: true);
        }
        if (!handle.Process.HasExited)
        {
            await handle.Process.WaitForExitAsync(cancellationToken);
        }
        _activeEmulatorHandle = null;
        await handle.DisposeAsync();
    }

    private static async Task DisposeHandlePreservingPrimaryFailureAsync(
        DetachedProcessHandle handle)
    {
        try
        {
            await handle.DisposeAsync();
        }
        catch
        {
            // Preserve the primary startup failure even if diagnostic capture itself failed.
        }
    }

    public async Task InstallApkAsync(string apkPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(apkPath))
        {
            throw new FileNotFoundException("找不到所选 APK。", apkPath);
        }

        await RequireRunningAsync(cancellationToken);
        var result = await _runner.RunAsync(
            AndroidCommandFactory.InstallApk(_layout, _options, apkPath),
            cancellationToken);
        EnsureSuccess(result, "安装 APK");
    }

    public async Task LaunchPackageAsync(string packageName, CancellationToken cancellationToken = default)
    {
        await RequireRunningAsync(cancellationToken);
        var result = await _runner.RunAsync(
            AndroidCommandFactory.LaunchPackage(_layout, _options, AndroidPackageName.Parse(packageName)),
            cancellationToken);
        EnsureSuccess(result, "启动应用");
    }

    public async Task<IReadOnlyList<string>> ListThirdPartyPackagesAsync(
        CancellationToken cancellationToken = default)
    {
        await RequireRunningAsync(cancellationToken);
        var result = await _runner.RunAsync(
            AndroidCommandFactory.Adb(_layout, _options, "shell", "pm", "list", "packages", "-3"),
            cancellationToken);
        EnsureSuccess(result, "读取第三方应用列表");
        return AndroidPackageListParser.Parse(result.StandardOutput);
    }

    public async Task ForceStopPackageAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        await RequireRunningAsync(cancellationToken);
        var result = await _runner.RunAsync(
            AndroidCommandFactory.ForceStopPackage(
                _layout,
                _options,
                AndroidPackageName.Parse(packageName)),
            cancellationToken);
        EnsureSuccess(result, "停止应用");
        using var exitDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        exitDeadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var pid = await _runner.RunAsync(AndroidCommandFactory.Adb(_layout, _options, "shell", "pidof", packageName), exitDeadline.Token);
            if (pid.ExitCode != 0 && !string.IsNullOrWhiteSpace(pid.StandardError)) EnsureSuccess(pid, "确认应用进程退出");
            if (string.IsNullOrWhiteSpace(pid.StandardOutput)) return;
            await Task.Delay(100, exitDeadline.Token);
        }
    }

    public async Task UninstallPackageAsync(
        string packageName,
        CancellationToken cancellationToken = default)
    {
        await RequireRunningAsync(cancellationToken);
        var result = await _runner.RunAsync(
            AndroidCommandFactory.UninstallPackage(
                _layout,
                _options,
                AndroidPackageName.Parse(packageName)),
            cancellationToken);
        EnsureSuccess(result, "卸载应用");
    }

    public async Task<string> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        if (!_layout.HasRequiredTools)
        {
            return $"Android SDK：未安装\n预期路径：{_layout.Root}";
        }

        var status = await GetStatusAsync(cancellationToken);
        if (status != VmStatus.Running)
        {
            return $"Android SDK：正常\n虚拟机：{LauncherDashboardState.From(status).StatusTitle}\nRoot：等待虚拟机启动";
        }

        var root = await _runner.RunAsync(AndroidCommandFactory.RootIdentity(_layout, _options), cancellationToken);
        var rootOk = root.ExitCode == 0 && root.StandardOutput.Contains("uid=0", StringComparison.Ordinal);
        return $"Android SDK：正常\n虚拟机：运行中\nADB：已连接\nRoot：{(rootOk ? "正常（uid=0）" : "异常")}";
    }

    private async Task RequireRunningAsync(CancellationToken cancellationToken)
    {
        if (await GetStatusAsync(cancellationToken) != VmStatus.Running)
        {
            throw new InvalidOperationException("请先启动安卓虚拟机。");
        }
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new InvalidOperationException($"{operation}失败：{detail}");
        }
    }

}
