using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Android;

public sealed class MagiskCliPatchService
{
    private const string WorkDirectory = "/data/data/com.android.shell/Magisk";
    private readonly AndroidSdkLayout _layout;
    private readonly AndroidVmOptions _options;
    private readonly IProcessRunner _runner;

    public MagiskCliPatchService(
        AndroidSdkLayout layout,
        AndroidVmOptions options,
        IProcessRunner? runner = null)
    {
        _layout = layout;
        _options = options;
        _runner = runner ?? new ProcessRunner();
    }

    public static string BuildPatchScript() =>
        """
        #!/system/bin/sh
        set -e
        WORK=/data/data/com.android.shell/Magisk
        cd "$WORK"
        cp -f assets/boot_patch.sh .
        cp -f assets/util_functions.sh .
        export KEEPVERITY=true
        export KEEPFORCEENCRYPT=true
        export RECOVERYMODE=false
        ./busybox sh ./boot_patch.sh /sdcard/Download/fakeboot.img
        test -s new-boot.img
        cp -f new-boot.img /sdcard/Download/magisk_patched-rgvm.img
        """;

    public async Task PatchAsync(CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var localScript = Path.Combine(Path.GetTempPath(), $"rgvm-direct-patch-{operationId}.sh");
        var remoteScript = $"{WorkDirectory}/rgvm-direct-patch-{operationId}.sh";
        try
        {
            await File.WriteAllBytesAsync(
                localScript,
                UnixShellScriptEncoding.Encode(BuildPatchScript()),
                cancellationToken);
            EnsureSuccess(await _runner.RunAsync(
                AndroidCommandFactory.Adb(
                    _layout,
                    _options,
                    "push",
                    localScript,
                    remoteScript),
                cancellationToken), "准备 Magisk 命令行补丁");
            EnsureSuccess(await _runner.RunAsync(
                AndroidCommandFactory.Adb(
                    _layout,
                    _options,
                    "shell",
                    "sh",
                    remoteScript),
                cancellationToken), "执行 Magisk boot_patch.sh");
            EnsureSuccess(await _runner.RunAsync(
                AndroidCommandFactory.Adb(
                    _layout,
                    _options,
                    "shell",
                    "test",
                    "-s",
                    "/sdcard/Download/magisk_patched-rgvm.img"),
                cancellationToken), "验证 Magisk 命令行补丁");
        }
        finally
        {
            File.Delete(localScript);
            try
            {
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
            catch
            {
                // RootAVD clears the work directory during its final pass.
            }
        }
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
