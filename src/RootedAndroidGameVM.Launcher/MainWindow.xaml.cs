using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Launcher;

public partial class MainWindow : Window
{
    private AndroidVmController _controller = new();
    private VmStatus _status = VmStatus.NotInstalled;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        ApplyState(LauncherDashboardState.From(_status));
        Loaded += async (_, _) => await RefreshStatusAsync();
    }

    private void ApplyState(LauncherDashboardState state)
    {
        _status = state.Status;
        StatusTitleText.Text = state.StatusTitle;
        StatusDetailText.Text = state.StatusDetail;
        PrimaryActionButton.Content = state.PrimaryActionText;
        StatusDot.Fill = new SolidColorBrush(state.Status switch
        {
            VmStatus.Running => Color.FromRgb(65, 204, 138),
            VmStatus.Stopped => Color.FromRgb(255, 185, 72),
            _ => Color.FromRgb(138, 148, 166)
        });
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            FooterStatusText.Text = "正在检查运行环境…";
            ApplyState(LauncherDashboardState.From(await _controller.GetStatusAsync()));
            FooterStatusText.Text = "准备就绪";
        }
        catch (Exception exception)
        {
            FooterStatusText.Text = $"检查失败：{exception.Message}";
        }
    }

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_status == VmStatus.NotInstalled)
        {
            MessageBox.Show(this, "尚未检测到已安装的虚拟机，请先运行图形安装器。", "需要安装",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunBusyAsync(
            _status == VmStatus.Running ? "正在停止虚拟机…" : "正在启动虚拟机…",
            _status == VmStatus.Running
                ? () => _controller.StopAsync()
                : () => _controller.StartAsync());
    }

    private async void ActionCard_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string action }) return;
        switch (action)
        {
            case "StartOrStop":
                PrimaryAction_Click(sender, e);
                break;
            case "InstallApk":
                await InstallApkAsync();
                break;
            case "AppsAndData":
                new DataAccessWindow(_controller) { Owner = this }.ShowDialog();
                break;
            case "Settings":
                await ShowSettingsAsync();
                break;
            case "Diagnostics":
                await ShowDiagnosticsAsync();
                break;
            case "Repair":
                OpenRepair();
                break;
        }
    }

    private async Task InstallApkAsync()
    {
        if (_status != VmStatus.Running)
        {
            MessageBox.Show(this, "请先启动安卓虚拟机，再安装 APK。", "虚拟机未运行",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要安装或更新的 APK",
            Filter = "Android 安装包 (*.apk)|*.apk",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        await RunBusyAsync("正在安装 APK…", () => _controller.InstallApkAsync(dialog.FileName),
            successMessage: "APK 已成功安装或更新。", refreshStatus: false);
    }

    private async Task ShowDiagnosticsAsync()
    {
        try
        {
            FooterStatusText.Text = "正在检查 ADB 与 Root…";
            var report = await _controller.DiagnoseAsync();
            MessageBox.Show(this, report, "环境诊断", MessageBoxButton.OK,
                report.Contains("异常", StringComparison.Ordinal) ? MessageBoxImage.Warning : MessageBoxImage.Information);
            FooterStatusText.Text = "诊断完成";
        }
        catch (Exception exception)
        {
            ShowError("诊断失败", exception);
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (_status == VmStatus.Running)
        {
            MessageBox.Show(this, "请先停止虚拟机，再切换图形性能档位。", "性能设置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var current = PerformanceProfileService.ReadCurrent();
        var result = MessageBox.Show(
            this,
            $"当前档位：{(current == PerformanceProfile.Stable ? "稳定（SwiftShader）" : "高性能（Host GPU）")}。\n\n" +
            "选择“是”切换到高性能 Host GPU；选择“否”切换到稳定 SwiftShader；选择“取消”不更改。",
            "性能设置",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return;

        var profile = result == MessageBoxResult.Yes
            ? PerformanceProfile.HighPerformance
            : PerformanceProfile.Stable;
        try
        {
            await new PerformanceProfileService(AndroidVmOptions.Default).ApplyAsync(profile);
            _controller = new AndroidVmController();
            FooterStatusText.Text = profile == PerformanceProfile.HighPerformance
                ? "已切换到高性能 Host GPU，下次启动生效。"
                : "已切换到稳定 SwiftShader，下次启动生效。";
        }
        catch (Exception exception)
        {
            ShowError("设置失败", exception);
        }
    }

    private async void OpenSettings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private void OpenRepair()
    {
        var setupPath = Path.Combine(AppContext.BaseDirectory, "RootedAndroidGameVM.Setup.exe");
        if (!File.Exists(setupPath))
        {
            MessageBox.Show(this, "当前目录中找不到图形安装器，请重新运行 Release 安装包。",
                "修复不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(setupPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        });
        FooterStatusText.Text = "修复安装器已打开。";
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                    e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
                    string.Equals(Path.GetExtension(files[0]), ".apk", StringComparison.OrdinalIgnoreCase)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files ||
            !string.Equals(Path.GetExtension(files[0]), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_status != VmStatus.Running)
        {
            MessageBox.Show(this, "请先启动安卓虚拟机，再拖入 APK。", "虚拟机未运行",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunBusyAsync(
            "正在安装拖入的 APK…",
            () => _controller.InstallApkAsync(files[0]),
            successMessage: "APK 已成功安装或更新。",
            refreshStatus: false);
    }

    private async Task RunBusyAsync(
        string busyText,
        Func<Task> operation,
        string? successMessage = null,
        bool refreshStatus = true)
    {
        if (_busy) return;
        _busy = true;
        PrimaryActionButton.IsEnabled = false;
        FooterStatusText.Text = busyText;
        try
        {
            await operation();
            if (refreshStatus) await RefreshStatusAsync();
            FooterStatusText.Text = successMessage ?? "操作完成";
            if (successMessage is not null)
            {
                MessageBox.Show(this, successMessage, "操作完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "操作已取消";
        }
        catch (Exception exception)
        {
            ShowError("操作失败", exception);
        }
        finally
        {
            _busy = false;
            PrimaryActionButton.IsEnabled = true;
        }
    }

    private void ShowError(string title, Exception exception)
    {
        var message = LogRedactor.RedactLocalPaths(exception.Message);
        FooterStatusText.Text = message;
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
