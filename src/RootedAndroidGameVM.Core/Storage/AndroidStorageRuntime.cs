using System.Diagnostics;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Storage;

public sealed class AndroidStorageRuntime
{
    public async Task StopAsync(InstallPaths paths, CancellationToken cancellationToken)
    {
        var layout = AndroidSdkLayout.FromRoot(paths.SdkRoot);
        var options = AndroidVmOptions.ForPaths(paths);
        var qemuPath = Path.Combine(paths.SdkRoot, "emulator", "qemu", "windows-x86_64", "qemu-system-x86_64.exe");
        var processes = FindProcesses("qemu-system-x86_64", qemuPath);
        try
        {
            if (processes.Count > 0)
            {
                var controller = new AndroidVmController(layout, options);
                if (await controller.GetStatusAsync(cancellationToken).ConfigureAwait(false) != VmStatus.Running)
                    throw new InvalidOperationException("资源目录中的模拟器仍在运行，但无法确认设备身份。请关闭该模拟器后重试。");
                await controller.StopAsync(cancellationToken).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(40));
                foreach (var process in processes)
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        // Release only tools loaded from this resource root. Other SDKs/AVDs are never selected.
        foreach (var process in FindProcesses("adb", layout.AdbPath))
        {
            using (process)
            {
                if (process.HasExited) continue;
                process.Kill();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task VerifyAsync(InstallPaths paths, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(paths.ProductRoot, "install.json"))) return; // Resumable, incomplete first installation.
        var layout = AndroidSdkLayout.FromRoot(paths.SdkRoot);
        var options = AndroidVmOptions.ForPaths(paths);
        var controller = new AndroidVmController(layout, options);
        await controller.StartAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = await controller.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
        if (!diagnostics.Contains("Root：正常（uid=0）", StringComparison.Ordinal))
            throw new InvalidOperationException("新位置的虚拟机未通过 Root 验证；原目录仍然保留。" + Environment.NewLine + diagnostics);
        var result = await new ProcessRunner().RunAsync(AndroidCommandFactory.Adb(layout, options,
            "shell", "su", "-c", "test -d /data/data"), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("新位置的私有数据目录不可访问，原目录仍然保留。");
    }

    private static List<Process> FindProcesses(string name, string executablePath)
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (!process.HasExited && string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                    matches.Add(process);
                else process.Dispose();
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
            catch
            {
                process.Dispose();
                foreach (var match in matches) match.Dispose();
                throw;
            }
        }
        return matches;
    }
}
