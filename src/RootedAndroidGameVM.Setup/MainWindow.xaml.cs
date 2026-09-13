using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Ui;
using RootedAndroidGameVM.Core.Storage;
using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Setup;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _installationCancellation;
    private bool _installationSucceeded;
    private readonly ProductStorageLocation _location;
    private readonly bool _createShortcuts;
    private readonly int _port;
    private bool _programUpdateOnly;

    public MainWindow(ProductStorageLocation? location = null, bool createShortcuts = true, int port = 5554, bool programUpdate = false)
    {
        _location = location ?? new ProductStorageLocation();
        _createShortcuts = createShortcuts;
        _port = port;
        InitializeComponent();
        StageList.ItemsSource = SetupProgressCatalog.All
            .Where(state => state.Stage != SetupStage.Complete)
            .Select((state, index) => new
            {
                Number = (index + 1).ToString(),
                state.Title,
                Caption = state.Detail
            });
        Closing += MainWindow_Closing;
        RefreshResourceSelection();
        if (programUpdate)
        {
            InstallButton.IsEnabled = false;
            Loaded += async (_, _) => await CheckProgramUpgradeAsync();
        }
    }

    private async Task CheckProgramUpgradeAsync()
    {
        try
        {
            using var lease = StorageOperationLease.Acquire(_location);
            _programUpdateOnly = await ProgramUpgradeProbe.CanReuseAsync(InstallPaths.FromProductRoot(_location.ReadRoot()));
            if (!_programUpdateOnly) return;
            HeadingText.Text = "程序已更新";
            SubtitleText.Text = "已识别原有运行环境，可以继续使用同一个模拟器。";
            ProgressTitleText.Text = "现有资源已保留";
            ProgressDetailText.Text = "无需重新下载或创建模拟器。资源位置和安卓应用数据保持不变。";
            ProgressPercentText.Text = "100%";
            InstallProgressBar.Value = 100;
            InstallButton.Content = "完成更新";
            LicenseCheckBox.Visibility = Visibility.Collapsed;
            LicenseLinkText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) { ProgressDetailText.Text = LogRedactor.RedactLocalPaths(exception.Message); }
        finally { InstallButton.IsEnabled = true; }
    }

    private void RefreshResourceSelection()
    {
        var root = _location.ReadRoot();
        ResourcePathTextBox.Text = root;
        var existingResources = Directory.Exists(root) && StorageOwnership.IsOwned(root);
        ResourcePathTextBox.IsReadOnly = existingResources;
        BrowseResourceButton.IsEnabled = !existingResources;
        if (!existingResources) return;
        HeadingText.Text = "更新或修复运行环境";
        InstallButton.Content = "更新并验证";
        StorageDescriptionText.Text = "更新会保留这个目录中的模拟器和应用数据。需要更换磁盘时，请在启动器的“资源位置”中迁移。";
        ProgressDetailText.Text = "沿用现有资源，检查所需组件和 Root。";
    }

    private void BrowseResource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择资源存储文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true) ResourcePathTextBox.Text = dialog.FolderName;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_programUpdateOnly)
        {
            if (_createShortcuts)
                ShortcutService.CreateLauncherStartMenuShortcut(Path.Combine(AppContext.BaseDirectory, "RootedAndroidGameVM.exe"));
            _installationSucceeded = true;
            Close();
            return;
        }
        if (LicenseCheckBox.IsChecked != true)
        {
            MessageBox.Show(this, "请先勾选接受 Android SDK 许可协议。", "需要接受许可",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _installationCancellation = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        LicenseCheckBox.IsEnabled = false;
        ExitButton.Content = "取消";
        var progress = new Progress<SetupProgressState>(ApplyProgress);
        try
        {
            using var lease = StorageOperationLease.Acquire(_location);
            var selectedRoot = ResourcePathTextBox.Text.Trim();
            await StorageOwnership.InitializeAsync(selectedRoot, _location, _installationCancellation.Token);
            ResourcePathTextBox.IsReadOnly = true;
            BrowseResourceButton.IsEnabled = false;
            var paths = InstallPaths.FromProductRoot(selectedRoot);
            var options = AndroidVmOptions.ForPaths(paths) with { Port = _port, Serial = $"emulator-{_port}" };
            await new RootedVmInstaller(paths, options: options).InstallAsync(
                sdkLicenseAccepted: true,
                progress,
                _installationCancellation.Token);
            if (_createShortcuts)
                ShortcutService.CreateLauncherStartMenuShortcut(
                    Path.Combine(AppContext.BaseDirectory, "RootedAndroidGameVM.exe"));
            _installationSucceeded = true;
            InstallButton.Content = "安装完成";
            ProgressTitleText.Text = "安装完成";
            ProgressDetailText.Text = "Root、ADB 与虚拟机启动均已通过验证。现在可以关闭安装器。";
            MessageBox.Show(this, "安装与 Root 验证已完成。以后直接双击桌面启动器即可。",
                "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgressTitleText.Text = "安装已取消";
            ProgressDetailText.Text = "已保留完成下载的校验缓存，下次可以继续。";
            InstallButton.Content = "继续安装";
            InstallButton.IsEnabled = true;
            LicenseCheckBox.IsEnabled = true;
        }
        catch (Exception exception)
        {
            var message = LogRedactor.RedactLocalPaths(exception.Message);
            ProgressTitleText.Text = "安装未完成";
            ProgressDetailText.Text = message;
            InstallButton.Content = "重试";
            InstallButton.IsEnabled = true;
            LicenseCheckBox.IsEnabled = true;
            MessageBox.Show(this, message, "安装失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _installationCancellation?.Dispose();
            _installationCancellation = null;
            ExitButton.Content = "退出";
            ExitButton.IsEnabled = true;
        }
    }

    private void ApplyProgress(SetupProgressState state)
    {
        ProgressTitleText.Text = state.Title;
        ProgressDetailText.Text = state.Detail;
        ProgressPercentText.Text = $"{state.Percent}%";
        InstallProgressBar.Value = state.Percent;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_installationCancellation is not null)
        {
            _installationCancellation.Cancel();
            ExitButton.IsEnabled = false;
            ProgressDetailText.Text = "正在安全停止当前步骤…";
            return;
        }

        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_installationCancellation is null) return;
        e.Cancel = true;
        _installationCancellation.Cancel();
        ExitButton.IsEnabled = false;
        ProgressDetailText.Text = "正在安全停止当前步骤…";
    }

    protected override void OnClosed(EventArgs e)
    {
        Environment.ExitCode = _installationSucceeded ? 0 : 1;
        base.OnClosed(e);
    }

    private void LicenseLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
