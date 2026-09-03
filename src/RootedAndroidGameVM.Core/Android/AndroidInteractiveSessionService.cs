using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Android;

public sealed class AndroidInteractiveSessionService(
    AndroidSdkLayout layout,
    AndroidVmOptions options,
    IProcessRunner runner)
{
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        EnsureSuccess(
            await runner.RunAsync(
                AndroidCommandFactory.WakeDevice(layout, options),
                cancellationToken),
            "唤醒安卓虚拟机");
        EnsureSuccess(
            await runner.RunAsync(
                AndroidCommandFactory.DismissKeyguard(layout, options),
                cancellationToken),
            "解除安卓虚拟机锁屏");
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
