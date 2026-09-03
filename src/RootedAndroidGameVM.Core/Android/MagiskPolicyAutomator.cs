using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Android;

public sealed class MagiskPolicyAutomator
{
    private const string PolicyIndicatorResourceId = "com.topjohnwu.magisk:id/policy_indicator";
    private readonly AndroidSdkLayout _layout;
    private readonly AndroidVmOptions _options;
    private readonly IProcessRunner _runner;

    public MagiskPolicyAutomator(
        AndroidSdkLayout layout,
        AndroidVmOptions options,
        IProcessRunner? runner = null)
    {
        _layout = layout;
        _options = options;
        _runner = runner ?? new ProcessRunner();
    }

    public async Task GrantShellAsync(CancellationToken cancellationToken = default)
    {
        var initial = await OpenMagiskAsync(includeAdditionalSetup: true, cancellationToken);
        if (initial.Contains("Requires additional setup"))
        {
            await TapAsync(await WaitForLabelAsync("OK", cancellationToken), cancellationToken);
            await WaitForBootAsync(cancellationToken);
            initial = await OpenMagiskAsync(includeAdditionalSetup: false, cancellationToken);
        }

        if (initial.Contains("Allow Magisk to send you notifications?"))
        {
            await TapAsync(await WaitForLabelAsync("Allow", cancellationToken), cancellationToken);
        }

        var granted = await TryGrantShellPromptAsync(cancellationToken);

        var beforePolicy = await OpenMagiskAsync(includeAdditionalSetup: true, cancellationToken);
        if (beforePolicy.Contains("Requires additional setup"))
        {
            await TapAsync(await WaitForLabelAsync("OK", cancellationToken), cancellationToken);
            await WaitForBootAsync(cancellationToken);
            beforePolicy = await OpenMagiskAsync(includeAdditionalSetup: false, cancellationToken);
            granted = await TryGrantShellPromptAsync(cancellationToken);
            beforePolicy = await OpenMagiskAsync(includeAdditionalSetup: false, cancellationToken);
        }
        if (granted)
        {
            await PersistShellPolicyAsync(cancellationToken);
            return;
        }

        await TapAsync(await WaitForLabelAsync("Superuser", cancellationToken), cancellationToken);
        var policySnapshot = await WaitForSnapshotAsync(
            snapshot => snapshot.Contains("[SharedUID] Shell"),
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!policySnapshot.IsCheckedByResourceId(PolicyIndicatorResourceId))
        {
            await TapAsync(await WaitForResourceAsync(PolicyIndicatorResourceId, cancellationToken), cancellationToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        await PersistShellPolicyAsync(cancellationToken);
    }

    public Task PersistCurrentShellPolicyAsync(CancellationToken cancellationToken = default) =>
        PersistShellPolicyAsync(cancellationToken);

    private async Task PersistShellPolicyAsync(CancellationToken cancellationToken)
    {
        const string sql =
            "REPLACE INTO policies (uid, policy, until, logging, notification) " +
            "VALUES (2000, 2, 0, 1, 1);";
        const string multiuserSql =
            "REPLACE INTO settings (key, value) VALUES ('su_multiuser_mode', 2);";
        var operationId = Guid.NewGuid().ToString("N");
        var localScript = Path.Combine(Path.GetTempPath(), $"rgvm-policy-{operationId}.sh");
        var remoteScript = $"/data/local/tmp/rgvm-policy-{operationId}.sh";
        try
        {
            await File.WriteAllTextAsync(
                localScript,
                $"#!/system/bin/sh\n" +
                $"magisk --sqlite \"{sql}\"\n" +
                $"magisk --sqlite \"{multiuserSql}\"\n",
                cancellationToken);
            EnsureSuccess(await _runner.RunAsync(
                AndroidCommandFactory.Adb(
                    _layout,
                    _options,
                    "push",
                    localScript,
                    remoteScript),
                cancellationToken), "准备 Magisk 授权策略");
            EnsureSuccess(await _runner.RunAsync(
                AndroidCommandFactory.Adb(
                    _layout,
                    _options,
                    "shell",
                    "su",
                    "-c",
                    $"sh {remoteScript}"),
                cancellationToken), "持久化 Magisk Shell 授权");
        }
        finally
        {
            File.Delete(localScript);
            await _runner.RunAsync(
                AndroidCommandFactory.Adb(
                    _layout,
                    _options,
                    "shell",
                    "rm",
                    "-f",
                    remoteScript),
                CancellationToken.None);
        }
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var verify = await _runner.RunAsync(
            AndroidCommandFactory.RootIdentity(_layout, _options),
            cancellationToken);
        if (verify.ExitCode != 0 || !verify.StandardOutput.Contains("uid=0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Magisk Shell 授权写入后验证失败。");
        }
    }

    private async Task<bool> TryGrantShellPromptAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var rootTask = _runner.RunAsync(
            AndroidCommandFactory.RootIdentity(_layout, _options),
            timeout.Token);
        try
        {
            var prompt = await WaitForSnapshotAsync(
                snapshot => snapshot.Contains("Grant") || snapshot.Contains("Deny"),
                TimeSpan.FromSeconds(12),
                cancellationToken);
            if (prompt.Contains("Grant"))
            {
                await TapAsync(prompt.FindCenter("Grant"), cancellationToken);
            }
        }
        catch (TimeoutException)
        {
            // A remembered deny policy can make su return before a dialog is drawn.
        }

        try
        {
            var result = await rootTask.WaitAsync(timeout.Token);
            return result.ExitCode == 0 &&
                   result.StandardOutput.Contains("uid=0", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<AndroidUiSnapshot> OpenMagiskAsync(
        bool includeAdditionalSetup,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + (
            _options.Headless ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(40));
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await LaunchAsync(cancellationToken);
                return await WaitForSnapshotAsync(
                    snapshot => (includeAdditionalSetup && snapshot.Contains("Requires additional setup")) ||
                                snapshot.Contains("Allow Magisk to send you notifications?") ||
                                snapshot.Contains("Superuser"),
                    _options.Headless ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(5),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException(FormatOpenFailure(lastError), lastError);
    }

    public static string FormatOpenFailure(Exception? lastError) =>
        lastError is null
            ? "多次启动 Magisk 后仍未出现授权主页。"
            : $"多次启动 Magisk 后仍未出现授权主页。最后错误：{lastError.Message}";

    public static bool IsRecoverableSystemAppAnrDialog(AndroidUiSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return (snapshot.Contains("System UI isn't responding") ||
                snapshot.Contains("Digital Wellbeing isn't responding")) &&
               snapshot.Contains("Close app") &&
               snapshot.Contains("Wait");
    }

    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        var launch = await _runner.RunAsync(
            AndroidCommandFactory.LaunchPackage(
                _layout,
                _options,
                AndroidPackageName.Parse("com.topjohnwu.magisk")),
            cancellationToken);
        EnsureSuccess(launch, "启动 Magisk 授权页");
    }

    private async Task WaitForBootAsync(CancellationToken cancellationToken)
    {
        var offlineDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        var observedOffline = false;
        while (DateTimeOffset.UtcNow < offlineDeadline)
        {
            var state = await _runner.RunAsync(
                AndroidCommandFactory.Adb(_layout, _options, "get-state"),
                cancellationToken);
            if (state.ExitCode != 0 ||
                !string.Equals(state.StandardOutput.Trim(), "device", StringComparison.OrdinalIgnoreCase))
            {
                observedOffline = true;
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        if (!observedOffline)
        {
            throw new TimeoutException("Magisk 额外设置未触发预期的设备重启。");
        }

        var wait = await _runner.RunAsync(
            AndroidCommandFactory.Adb(_layout, _options, "wait-for-device"),
            cancellationToken);
        EnsureSuccess(wait, "等待 Magisk 额外设置重启");
        var deadline = DateTimeOffset.UtcNow + AndroidVmStartupPolicy.Default.Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var boot = await _runner.RunAsync(
                AndroidCommandFactory.Adb(
                    _layout,
                    _options,
                    "shell",
                    "getprop",
                    "sys.boot_completed"),
                cancellationToken);
            if (boot.ExitCode == 0 && boot.StandardOutput.Trim() == "1")
            {
                await new AndroidInteractiveSessionService(_layout, _options, _runner)
                    .PrepareAsync(cancellationToken);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException(
            $"Magisk 额外设置重启未在 {AndroidVmStartupPolicy.Default.Timeout.TotalMinutes:0} 分钟内完成。");
    }

    private async Task<AndroidUiPoint> WaitForLabelAsync(
        string label,
        CancellationToken cancellationToken) =>
        await WaitForPointAsync(
            snapshot => snapshot.FindCenter(label),
            TimeSpan.FromSeconds(30),
            cancellationToken);

    private async Task<AndroidUiPoint> WaitForResourceAsync(
        string resourceId,
        CancellationToken cancellationToken) =>
        await WaitForPointAsync(
            snapshot => snapshot.FindCenterByResourceId(resourceId),
            TimeSpan.FromSeconds(30),
            cancellationToken);

    private async Task<AndroidUiPoint> WaitForPointAsync(
        Func<AndroidUiSnapshot, AndroidUiPoint> selector,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        AndroidUiPoint? point = null;
        await WaitForSnapshotAsync(snapshot =>
        {
            try
            {
                point = selector(snapshot);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, timeout, cancellationToken);
        return point ?? throw new TimeoutException("等待 Magisk 控件超时。");
    }

    private async Task<AndroidUiSnapshot> WaitForSnapshotAsync(
        Func<AndroidUiSnapshot, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var lastVisibleLabels = "<none>";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                try
                {
                    var snapshot = AndroidUiSnapshot.Parse(dump.StandardOutput);
                    var labels = snapshot.DescribeVisibleLabels();
                    if (!string.IsNullOrWhiteSpace(labels)) lastVisibleLabels = labels;
                    if (IsRecoverableSystemAppAnrDialog(snapshot))
                    {
                        await TapAsync(snapshot.FindCenter("Wait"), cancellationToken);
                        continue;
                    }
                    if (predicate(snapshot)) return snapshot;
                }
                catch (InvalidDataException)
                {
                    // The activity can redraw while UIAutomator is serializing; retry with a fresh snapshot.
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException(
            $"等待 Magisk 授权界面超时。最后可见标签：{lastVisibleLabels}");
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
        EnsureSuccess(tap, "操作 Magisk 授权界面");
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
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
