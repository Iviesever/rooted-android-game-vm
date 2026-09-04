using System.IO;
using System.Text.Json;
using System.Windows;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--remove-resources" && e.Args[1] is "runtime" or "all")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RemoveResourcesAsync(e.Args[1]);
            return;
        }
        SetupE2eOptions? options;
        try
        {
            var gui = SetupGuiTestOptions.TryParse(e.Args, Environment.GetEnvironmentVariable);
            if (gui is not null)
            {
                new MainWindow(new ProductStorageLocation(gui.ControlRoot), createShortcuts: false, port: gui.Port).Show();
                return;
            }
            options = SetupE2eOptions.TryParse(
                e.Args,
                Environment.GetEnvironmentVariable);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                LogRedactor.RedactLocalPaths(exception.Message),
                "E2E 参数错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (options is null)
        {
            try { new MainWindow(programUpdate: e.Args.SequenceEqual(["--configure-or-upgrade"])).Show(); }
            catch (Exception exception)
            {
                MessageBox.Show(LogRedactor.RedactLocalPaths(exception.Message), "资源位置不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = RunE2eAsync(options);
    }

    private async Task RunE2eAsync(SetupE2eOptions options)
    {
        var paths = InstallPaths.FromProductRoot(options.ProductRoot);
        var vmOptions = new AndroidVmOptions(
            options.AvdName,
            options.Serial,
            options.Port,
            "swiftshader_indirect",
            4096,
            paths.AvdHome,
            Headless: options.Headless,
            Verbose: options.Headless);
        var resultPath = Path.Combine(paths.ProductRoot, "setup-exe-e2e-result.json");
        var controller = new AndroidVmController(AndroidSdkLayout.FromRoot(paths.SdkRoot), vmOptions);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
            await new RootedVmInstaller(paths, options: vmOptions).InstallAsync(
                sdkLicenseAccepted: true,
                cancellationToken: timeout.Token,
                adoptExistingEnvironment: false);
            ShortcutService.CreateLauncherStartMenuShortcut(
                Path.Combine(AppContext.BaseDirectory, "RootedAndroidGameVM.exe"));
            await File.WriteAllTextAsync(
                resultPath,
                JsonSerializer.Serialize(
                    new
                    {
                        success = true,
                        completedAtUtc = DateTimeOffset.UtcNow,
                        options.AvdName,
                        options.Port
                    },
                    new JsonSerializerOptions { WriteIndented = true }),
                timeout.Token);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(paths.ProductRoot);
            await File.WriteAllTextAsync(
                resultPath,
                JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        error = LogRedactor.RedactLocalPaths(exception.Message),
                        failedAtUtc = DateTimeOffset.UtcNow
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(1);
        }
        finally
        {
            try
            {
                await controller.StopAsync(CancellationToken.None);
            }
            catch
            {
                // The E2E result is already recorded; shutdown cleanup remains best effort.
            }
        }
    }

    private async Task RemoveResourcesAsync(string scope)
    {
        try
        {
            await new ResourceRemovalService().RemoveAsync(scope);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            MessageBox.Show(LogRedactor.RedactLocalPaths(exception.Message), "资源卸载未完成", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
