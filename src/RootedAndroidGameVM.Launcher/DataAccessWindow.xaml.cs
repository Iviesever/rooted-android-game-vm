using Microsoft.Win32;
using System.Windows;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Security;

namespace RootedAndroidGameVM.Launcher;

public partial class DataAccessWindow : Window
{
    private readonly AndroidVmController _controller;
    private readonly AndroidPrivateDataService _dataService = new();

    public DataAccessWindow(AndroidVmController controller)
    {
        _controller = controller;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshPackagesAsync();
    }

    private async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "正在打开 Material Files 文件管理器…";
            await _controller.LaunchPackageAsync("me.zhanghai.android.files");
            StatusText.Text = "文件管理器已打开。可使用 Root 权限浏览各应用私有目录。";
        }
        catch (Exception exception)
        {
            ShowError(new InvalidOperationException(
                "未能打开文件管理器。请先用首页“安装或更新 APK”安装 Material Files，再重试。",
                exception));
        }
    }

    private async void RefreshPackages_Click(object sender, RoutedEventArgs e) =>
        await RefreshPackagesAsync();

    private async Task RefreshPackagesAsync()
    {
        try
        {
            StatusText.Text = "正在读取第三方应用列表…";
            var packages = await _controller.ListThirdPartyPackagesAsync();
            PackageComboBox.ItemsSource = packages;
            if (packages.Count > 0)
            {
                PackageComboBox.SelectedIndex = 0;
            }

            StatusText.Text = $"已发现 {packages.Count} 个第三方应用。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void LaunchSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var packageName = GetSelectedPackage();
            await _controller.LaunchPackageAsync(packageName);
            StatusText.Text = $"已启动 {packageName}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void StopSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var packageName = GetSelectedPackage();
            await _controller.ForceStopPackageAsync(packageName);
            StatusText.Text = $"已停止 {packageName}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var packageName = GetSelectedPackage();
            var relativePath = AndroidRelativePath.Parse(RelativePathTextBox.Text).Value;
            if (MessageBox.Show(
                    this,
                    $"将以 Root 权限读取：\n/data/data/{packageName}/{relativePath}\n\n并复制到你选择的 Windows 文件夹。是否继续？",
                    "确认私有数据导出",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            await ExportAsync(packageName, relativePath, ExportSelectedButton);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void UninstallSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var packageName = GetSelectedPackage();
            if (MessageBox.Show(
                    this,
                    $"将卸载 {packageName} 并删除该应用在安卓虚拟机内的数据。此操作不可撤销。是否继续？",
                    "确认卸载应用",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            await _controller.UninstallPackageAsync(packageName);
            StatusText.Text = $"已卸载 {packageName}";
            await RefreshPackagesAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task ExportAsync(string packageName, string relativePath, System.Windows.Controls.Button button)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择私有数据的导出位置",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        button.IsEnabled = false;
        StatusText.Text = $"正在导出 /data/data/{packageName}/{relativePath}…";
        try
        {
            var exportedPath = await _dataService.ExportDirectoryAsync(
                packageName,
                relativePath,
                dialog.FolderName);
            StatusText.Text = $"导出完成：{exportedPath}";
            MessageBox.Show(this, $"数据已导出到：\n{exportedPath}", "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private string GetSelectedPackage()
    {
        var value = PackageComboBox.Text?.Trim();
        return AndroidPackageName.Parse(value ?? string.Empty).Value;
    }

    private void ShowError(Exception exception)
    {
        var message = LogRedactor.RedactLocalPaths(exception.Message);
        StatusText.Text = message;
        MessageBox.Show(this, message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
