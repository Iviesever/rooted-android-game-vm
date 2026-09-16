using System.Configuration;
using System.Data;
using System.Windows;
using System.Threading;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Launcher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(true, @"Local\RootedAndroidGameVM.Launcher", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("启动器已在运行，请使用已打开的窗口。", "Rooted Android Game VM");
            Shutdown();
            return;
        }
        try
        {
            // A recoverable transaction must remain reachable even when the active location is damaged or offline.
            if (MigrationJournal.Read(new ProductStorageLocation()) is not null)
                new StorageWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen }.Show();
            else
            {
                // The workbench is a low-rate control UI; avoid a second GPU/driver rendering context
                // when the VM is configured for a small memory footprint. Android still uses its GPU.
                if (new RootedAndroidGameVM.Core.Android.RuntimeProfileStore(
                    RootedAndroidGameVM.Core.Setup.InstallPaths.CreateDefault()).Read().LowRam)
                    System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                new WorkstationWindow().Show();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(LogRedactor.RedactLocalPaths(exception.Message), "资源位置不可用", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

