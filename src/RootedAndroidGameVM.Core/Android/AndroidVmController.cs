using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Android;

public sealed class AndroidVmController
{
    private readonly AndroidSdkLayout _layout;
    private readonly AndroidVmOptions _options;
    private readonly IProcessRunner _runner;
    private readonly DetachedProcessLauncher _detachedLauncher;

    public AndroidVmController(
        AndroidSdkLayout? layout = null,
        AndroidVmOptions? options = null,
        IProcessRunner? runner = null,
        DetachedProcessLauncher? detachedLauncher = null)
    {
        _layout = layout ?? AndroidSdkLayout.Discover();
        _options = options ?? AndroidVmOptions.Default;
        _runner = runner ?? new ProcessRunner();
        _detachedLauncher = detachedLauncher ?? new DetachedProcessLauncher();
    }

    public async Task<VmStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_layout.HasRequiredTools)
        {
            return VmStatus.NotInstalled;
        }

        var avds = await RunWithAvdEnvironmentAsync(
            new ProcessSpec(_layout.EmulatorPath, ["-list-avds"], Path.GetDirectoryName(_layout.EmulatorPath)),
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
            return;
        }

        using var emulatorProcess = _detachedLauncher.Start(
            AndroidCommandFactory.StartEmulator(_layout, _options),
            CreateAvdEnvironment());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            var wait = await _runner.RunAsync(
                AndroidCommandFactory.Adb(_layout, _options, "wait-for-device"),
                timeout.Token);
            EnsureSuccess(wait, "等待安卓虚拟机连接");

            for (var attempt = 0; attempt < 90; attempt++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var boot = await _runner.RunAsync(
                    AndroidCommandFactory.Adb(_layout, _options, "shell", "getprop", "sys.boot_completed"),
                    timeout.Token);
                if (boot.ExitCode == 0 && boot.StandardOutput.Trim() == "1")
                {
                    await _runner.RunAsync(
                        AndroidCommandFactory.Adb(_layout, _options, "shell", "settings", "put", "secure",
                            "show_ime_with_hard_keyboard", "0"),
                        timeout.Token);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            }

            throw new TimeoutException("安卓虚拟机未能在三分钟内完成启动。");
        }
        catch
        {
            if (!emulatorProcess.HasExited)
            {
                emulatorProcess.Kill(entireProcessTree: true);
                await emulatorProcess.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (await GetStatusAsync(cancellationToken) != VmStatus.Running)
        {
            return;
        }

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
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException("安卓虚拟机未能在一分钟内完全停止。");
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

    private Task<ProcessResult> RunWithAvdEnvironmentAsync(
        ProcessSpec spec,
        CancellationToken cancellationToken)
    {
        var environment = CreateAvdEnvironment();
        return environment is null
            ? _runner.RunAsync(spec, cancellationToken)
            : _runner.RunRequestAsync(
                new ProcessRequest(spec, EnvironmentVariables: environment),
                cancellationToken);
    }

    private IReadOnlyDictionary<string, string>? CreateAvdEnvironment() =>
        string.IsNullOrWhiteSpace(_options.AvdHome)
            ? null
            : new Dictionary<string, string>
            {
                ["ANDROID_AVD_HOME"] = _options.AvdHome
            };
}
