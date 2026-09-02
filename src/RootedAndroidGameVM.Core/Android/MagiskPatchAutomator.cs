using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Android;

public sealed class MagiskPatchAutomator
{
    private readonly AndroidSdkLayout _layout;
    private readonly AndroidVmOptions _options;
    private readonly IProcessRunner _runner;

    public MagiskPatchAutomator(
        AndroidSdkLayout layout,
        AndroidVmOptions options,
        IProcessRunner? runner = null)
    {
        _layout = layout;
        _options = options;
        _runner = runner ?? new ProcessRunner();
    }

    public async Task PatchFakeBootAsync(
        string magiskApkPath,
        CancellationToken cancellationToken = default)
    {
        var install = await _runner.RunAsync(
            AndroidCommandFactory.InstallApk(_layout, _options, magiskApkPath),
            cancellationToken);
        EnsureSuccess(install, "临时安装 Magisk");

        var first = await OpenMagiskInstallerHomeAsync(cancellationToken);
        if (first.Contains("Requires additional setup"))
        {
            await TapAsync(first.FindCenter("CANCEL"), cancellationToken);
            first = await WaitForSnapshotAsync(
                snapshot => CanFindEnabled(snapshot, "Install"),
                TimeSpan.FromSeconds(20),
                cancellationToken);
        }
        if (first.Contains("Allow Magisk to send you notifications?"))
        {
            await TapAsync(await WaitForCenterAsync("Allow", TimeSpan.FromSeconds(10), cancellationToken),
                cancellationToken);
            first = await OpenMagiskInstallerHomeAsync(cancellationToken);
            if (first.Contains("Requires additional setup"))
            {
                await TapAsync(first.FindCenter("CANCEL"), cancellationToken);
            }
        }

        await TapAsync(await WaitForCenterAsync("Install", TimeSpan.FromSeconds(30), cancellationToken),
            cancellationToken);
        await TapAsync(await WaitForCenterAsync("Select and patch a file", TimeSpan.FromSeconds(20), cancellationToken),
            cancellationToken);
        await TapAsync(await WaitForCenterAsync("Show roots", TimeSpan.FromSeconds(20), cancellationToken),
            cancellationToken);
        await TapAsync(await WaitForCenterAsync("Downloads", TimeSpan.FromSeconds(20), cancellationToken),
            cancellationToken);
        await TapAsync(await WaitForCenterAsync("fakeboot.img", TimeSpan.FromSeconds(20), cancellationToken),
            cancellationToken);
        await TapAsync(await WaitForCenterAsync("LET'S GO", TimeSpan.FromSeconds(20), cancellationToken),
            cancellationToken);

        await WaitForSnapshotAsync(
            snapshot => snapshot.Contains("- All done!"),
            TimeSpan.FromMinutes(2),
            cancellationToken);

        var patched = await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                _layout,
                _options,
                "shell",
                "ls",
                "/sdcard/Download/magisk_patched*.img"),
            cancellationToken);
        EnsureSuccess(patched, "验证 Magisk 补丁文件");
    }

    private async Task<AndroidUiSnapshot> OpenMagiskInstallerHomeAsync(
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var launch = await _runner.RunAsync(
                    AndroidCommandFactory.LaunchPackage(
                        _layout,
                        _options,
                        AndroidPackageName.Parse("com.topjohnwu.magisk")),
                    cancellationToken);
                EnsureSuccess(launch, "启动 Magisk");
                return await WaitForSnapshotAsync(
                    snapshot => snapshot.Contains("Allow Magisk to send you notifications?") ||
                                snapshot.Contains("Requires additional setup") ||
                                CanFindEnabled(snapshot, "Install"),
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
            {
                lastError = exception;
            }
        }

        throw new TimeoutException("多次启动 Magisk 后仍未出现补丁首页。", lastError);
    }

    private static bool CanFindEnabled(AndroidUiSnapshot snapshot, string label)
    {
        try
        {
            snapshot.FindCenter(label);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<AndroidUiPoint> WaitForCenterAsync(
        string label,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        AndroidUiPoint? point = null;
        await WaitForSnapshotAsync(snapshot =>
        {
            try
            {
                point = snapshot.FindCenter(label);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, timeout, cancellationToken);
        return point ?? throw new TimeoutException($"等待 Android UI 元素“{label}”超时。");
    }

    private async Task<AndroidUiSnapshot> WaitForSnapshotAsync(
        Func<AndroidUiSnapshot, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var dump = await _runner.RunAsync(
                    AndroidCommandFactory.Adb(
                        _layout,
                        _options,
                        "exec-out",
                        "uiautomator",
                        "dump",
                        "/dev/tty"),
                    cancellationToken);
                if (dump.ExitCode == 0)
                {
                    var snapshot = AndroidUiSnapshot.Parse(dump.StandardOutput);
                    if (predicate(snapshot)) return snapshot;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException("等待 Android 界面响应超时。", lastError);
    }

    private async Task TapAsync(AndroidUiPoint point, CancellationToken cancellationToken)
    {
        var tap = await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                _layout,
                _options,
                "shell",
                "input",
                "tap",
                point.X.ToString(),
                point.Y.ToString()),
            cancellationToken);
        EnsureSuccess(tap, "操作 Android 界面");
        await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException($"{operation}失败：{detail}");
    }
}
