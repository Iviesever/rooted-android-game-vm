using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Setup;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _installationCancellation;
    private bool _installationSucceeded;

    public MainWindow()
    {
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
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
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
            await new RootedVmInstaller().InstallAsync(
                sdkLicenseAccepted: true,
                progress,
                _installationCancellation.Token);
            ShortcutService.CreateLauncherShortcuts(
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
