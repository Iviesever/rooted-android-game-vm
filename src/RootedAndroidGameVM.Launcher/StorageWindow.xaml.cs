using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Launcher;

public partial class StorageWindow : Window
{
    private readonly ProductStorageLocation _location = new();
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _hasResources;
    private bool _hasPending;
    private long _totalBytes;
    private string _currentRoot = string.Empty;
    private long _lastProgressTick;
    private string _lastStage = string.Empty;

    public StorageWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closing += Window_Closing;
    }

    private async Task RefreshAsync()
    {
        try
        {
            _currentRoot = _location.ReadRoot();
            CurrentPathTextBox.Text = _currentRoot;
            var inventory = await Task.Run(() => Directory.Exists(_currentRoot)
                ? VerifiedDirectoryCopy.ReadInventory(_currentRoot, StorageOwnership.ControlFiles)
                : new ResourceInventory([], []));
            _totalBytes = inventory.TotalBytes;
            _hasResources = inventory.Files.Count > 0 && StorageOwnership.IsOwned(_currentRoot);
            CurrentSizeText.Text = $"{FormatBytes(_totalBytes)} · {inventory.Files.Count:N0} 个文件";
            var pending = MigrationJournal.Read(_location);
            _hasPending = pending is not null;
            RecoverButton.Visibility = _hasPending ? Visibility.Visible : Visibility.Collapsed;
            RecoverButton.Content = pending?.Stage is MigrationStage.Cleaning or MigrationStage.Verified
                ? "继续完成迁移 / 清理原副本" : "恢复未完成的迁移";
            UpdateButtons();
        }
        catch (Exception exception)
        {
            _hasResources = false;
            MoveButton.IsEnabled = false;
            ShowStatus("无法读取资源位置", exception.Message);
        }
    }

    private void TargetPath_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (MoveButton is not null) UpdateButtons();
    }

    private void UpdateButtons()
    {
        MoveButton.IsEnabled = !_busy && _hasResources && !_hasPending && !string.IsNullOrWhiteSpace(TargetPathTextBox.Text);
        BrowseButton.IsEnabled = !_busy && !_hasPending;
        TargetPathTextBox.IsEnabled = !_busy && !_hasPending;
        RecoverButton.IsEnabled = !_busy;
        CloseButton.IsEnabled = !_busy;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择资源迁移目标（空文件夹）", Multiselect = false };
        if (dialog.ShowDialog(this) == true) TargetPathTextBox.Text = dialog.FolderName;
    }

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var target = StoragePathPolicy.NormalizeRoot(TargetPathTextBox.Text.Trim());
            if (MessageBox.Show(this,
                    $"将迁移 {FormatBytes(_totalBytes)} 资源。\n\n当前位置：{_currentRoot}\n目标位置：{target}\n\n模拟器会关闭；新位置验证成功后，原资源将被清理。是否开始？",
                    "迁移资源", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await RunTransferAsync((service, progress, token) => service.MigrateAsync(target, progress, token));
        }
        catch (Exception exception) { ShowStatus("无法开始迁移", exception.Message); }
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var pending = MigrationJournal.Read(_location);
        if (pending is null) { await RefreshAsync(); return; }
        var message = pending.Stage is MigrationStage.Verified or MigrationStage.Cleaning
            ? "新位置已通过验证，将继续完成位置切换和原副本清理。是否继续？"
            : "将恢复原资源位置，并清理未提交的迁移副本。原始模拟器数据会保留。是否继续？";
        if (MessageBox.Show(this, message, "恢复资源迁移", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunTransferAsync((service, progress, token) => service.RecoverAsync(progress, token));
    }

    private async Task RunTransferAsync(Func<ResourceMigrationService, IProgress<ResourceTransferProgress>, CancellationToken,
        Task<ResourceMigrationResult>> operation)
    {
        _busy = true;
        _cancellation = new CancellationTokenSource();
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        UpdateButtons();
        try
        {
            var result = await operation(new ResourceMigrationService(_location),
                new Progress<ResourceTransferProgress>(ApplyProgress), _cancellation.Token);
            TransferProgressBar.IsIndeterminate = false;
            TransferProgressBar.Value = 100;
            ShowStatus(result.HasPendingCleanup ? "新位置已生效，原副本尚有残留" : "资源操作已完成",
                result.HasPendingCleanup
                    ? $"已释放原目录中的 {FormatBytes(result.ReclaimedBytes)}。还有 {result.RemainingSourceFiles.Count} 个占用或变化的文件被保留，可重试清理。"
                    : $"当前资源位置：{result.ResourceRoot}\n已从原目录释放 {FormatBytes(result.ReclaimedBytes)}。可关闭此窗口并启动模拟器。");
        }
        catch (OperationCanceledException) { ShowStatus("迁移已停止", "原资源仍保留。请使用下方恢复按钮处理未完成的副本。"); }
        catch (Exception exception) { ShowStatus("资源操作未完成", exception.Message + "\n请使用恢复按钮继续处理；不要手动删除资源目录。"); }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _busy = false;
            CancelButton.Visibility = Visibility.Collapsed;
            TransferProgressBar.IsIndeterminate = false;
            await RefreshAsync();
        }
    }

    private void ApplyProgress(ResourceTransferProgress progress)
    {
        var now = Stopwatch.GetTimestamp();
        if (progress.Stage == _lastStage && Stopwatch.GetElapsedTime(_lastProgressTick, now).TotalMilliseconds < 100) return;
        _lastProgressTick = now;
        _lastStage = progress.Stage;
        ProgressTitleText.Text = progress.Stage;
        ProgressDetailText.Text = progress.Detail;
        TransferProgressBar.IsIndeterminate = progress.TotalBytes <= 0 || progress.Stage == "正在验证新位置";
        TransferProgressBar.Value = progress.TotalBytes == 0 ? 0 : 100d * progress.CompletedBytes / progress.TotalBytes;
        ProgressBytesText.Text = progress.TotalBytes > 0 ? $"{FormatBytes(progress.CompletedBytes)} / {FormatBytes(progress.TotalBytes)}" : string.Empty;
        if (progress.Stage == "正在清理原资源") CancelButton.IsEnabled = false;
    }

    private void ShowStatus(string title, string detail)
    {
        ProgressTitleText.Text = title;
        ProgressDetailText.Text = LogRedactor.RedactLocalPaths(detail);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressDetailText.Text = "正在安全停止当前操作…";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; ProgressDetailText.Text = "请等待操作完成，或先取消迁移。"; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private static string FormatBytes(long bytes) => $"{bytes / (1024d * 1024 * 1024):F2} GB";
}
