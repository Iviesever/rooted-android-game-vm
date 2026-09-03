using System.IO;
using System.Text.Json;
using System.Windows;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SetupE2eOptions? options;
        try
        {
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
            new MainWindow().Show();
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
            paths.AvdHome);
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
}
