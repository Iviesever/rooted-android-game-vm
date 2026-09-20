using Microsoft.Win32;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Ui.Workstation;
using RootedAndroidGameVM.Launcher.Workstation;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TouchPoint = RootedAndroidGameVM.Core.Debugging.TouchPoint;

namespace RootedAndroidGameVM.Launcher;

public partial class WorkstationWindow : Window
{
    public WorkstationViewModel ViewModel { get; }
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly BoundedLogTail _logTail = new();
    private bool _readingPreview, _readingLog, _sendingGesture;
    private PreviewMetadata? _shownMetadata, _gestureMetadata;
    private Point? _gestureStart;
    private Stopwatch? _gestureClock;
    private readonly bool _offline;

    public WorkstationWindow() : this(new BrokerWorkstationApi()) { }
    private void OpenSessionRecords_Click(object sender, RoutedEventArgs e)
    {
        var directory = ViewModel.SessionArtifactDirectory;
        if (Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
    public WorkstationWindow(IWorkstationApi api, bool offline = false)
    {
        _offline = offline;
        ViewModel = new(api);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ModelChanged;
        Loaded += async (_, _) =>
        {
            if (_offline) return;
            await ViewModel.RefreshAsync();
            await ViewModel.RefreshRuntimeAsync();
            _statusTimer.Start(); _previewTimer.Start(); _logTimer.Start();
        };
        _statusTimer.Tick += async (_, _) => await ViewModel.RefreshAsync();
        _previewTimer.Tick += async (_, _) =>
        {
            if (_readingPreview || _gestureStart is not null || _sendingGesture || !IsActive || WindowState == WindowState.Minimized) return;
            _readingPreview = true;
            try { await ViewModel.RefreshPreviewAsync(); }
            finally { _readingPreview = false; }
        };
        _logTimer.Tick += async (_, _) =>
        {
            if (_readingLog || ViewModel.Section != WorkstationSection.Diagnostics) return;
            var item = ViewModel.Work.FirstOrDefault(work => work.Title == "应用日志" && work.Directory is not null);
            if (item?.Directory is null) return;
            _readingLog = true;
            try
            {
                var text = await _logTail.ReadAsync(Path.Combine(item.Directory, "events.ndjson"));
                if (text is not null) ViewModel.LogText = text;
            }
            catch (IOException error) { ViewModel.Message = "日志暂不可读：" + error.Message; }
            finally { _readingLog = false; }
        };
        Closed += (_, _) =>
        {
            _statusTimer.Stop(); _previewTimer.Stop(); _logTimer.Stop();
            ViewModel.PropertyChanged -= ModelChanged; ViewModel.Dispose();
        };
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.SelectedNavigation) && !_offline) _ = LoadSectionAsync();
        if (args.PropertyName == nameof(ViewModel.Preview) && ViewModel.Preview is { } frame)
            ShowImage(frame.Payload, frame.Metadata);
        if (args.PropertyName == nameof(ViewModel.Observation) && ViewModel.Observation is { } screen)
        {
            var nativeWidth = screen.ImageRotation is 90 or 270 ? screen.Height : screen.Width;
            var nativeHeight = screen.ImageRotation is 90 or 270 ? screen.Width : screen.Height;
            ShowImage(File.ReadAllBytes(screen.Path), new(screen.Session, screen.Width, screen.Height, nativeWidth, nativeHeight,
                screen.Rotation, screen.ImageRotation, screen.Foreground, screen.Awake, screen.Locked, screen.CapturedAt, 0));
        }
    }
    private async Task LoadSectionAsync()
    {
        try
        {
            switch (ViewModel.Section)
            {
                case WorkstationSection.Applications when ViewModel.IsRunning: await ViewModel.RefreshApplicationsAsync(); break;
                case WorkstationSection.Files when ViewModel.IsRunning: await ViewModel.RefreshApplicationsAsync(); await ViewModel.BrowseFilesAsync(); break;
                case WorkstationSection.Diagnostics when ViewModel.IsRunning: await ViewModel.RefreshApplicationsAsync(); break;
                case WorkstationSection.Checkpoints: await ViewModel.RefreshCheckpointsAsync(); break;
                case WorkstationSection.Settings: await ViewModel.RefreshRuntimeAsync(); break;
            }
        }
        catch (Exception error) { ViewModel.Message = error.Message; }
    }
    private void ShowImage(ReadOnlyMemory<byte> bytes, PreviewMetadata metadata)
    {
        var segment = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out var array) ? array : new ArraySegment<byte>(bytes.ToArray());
        using var stream = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        PreviewImage.Source = bitmap;
        PreviewImage.LayoutTransform = new RotateTransform(metadata.ImageRotation - metadata.Rotation);
        _shownMetadata = metadata;
    }
    private Point? NormalizedPoint(Point point)
    {
        if (_shownMetadata is not { } meta || PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0) return null;
        var scale = Math.Min(PreviewImage.ActualWidth / meta.Width, PreviewImage.ActualHeight / meta.Height);
        var width = meta.Width * scale; var height = meta.Height * scale;
        var x = (point.X - (PreviewImage.ActualWidth - width) / 2) / width;
        var y = (point.Y - (PreviewImage.ActualHeight - height) / 2) / height;
        return x is >= 0 and <= 1 && y is >= 0 and <= 1 ? new Point(x, y) : null;
    }
    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        var point = NormalizedPoint(e.GetPosition(PreviewImage));
        if (point is { } p && _shownMetadata is { } meta)
            CoordinateText.Text = $"{p.X * meta.Width:0}, {p.Y * meta.Height:0} · 调试手势在松开后发送";
    }
    private void Preview_Down(object sender, MouseButtonEventArgs e)
    {
        if (_sendingGesture) return;
        _gestureStart = NormalizedPoint(e.GetPosition(PreviewImage)); _gestureMetadata = _shownMetadata;
        if (_gestureStart is not null) { _gestureClock = Stopwatch.StartNew(); PreviewImage.CaptureMouse(); }
    }
    private async void Preview_Up(object sender, MouseButtonEventArgs e)
    {
        var end = NormalizedPoint(e.GetPosition(PreviewImage)); PreviewImage.ReleaseMouseCapture();
        var start = _gestureStart; var metadata = _gestureMetadata; var clock = _gestureClock;
        _gestureStart = null; _gestureMetadata = null; _gestureClock = null;
        if (start is not { } first || end is not { } last || metadata is null || clock is null) return;
        _sendingGesture = true;
        try { await ViewModel.GestureAsync(metadata, first.X, first.Y, last.X, last.Y, (int)clock.ElapsedMilliseconds); }
        catch (Exception error) { ViewModel.Message = error.Message; }
        finally { _sendingGesture = false; }
    }
    private async void BrowseApk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Android 应用|*.apk", Title = "选择要安装的 APK" };
        if (dialog.ShowDialog(this) == true) { ViewModel.ApkPath = dialog.FileName; await ViewModel.InspectApkAsync(); }
    }
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "上传到当前安卓目录" };
        if (dialog.ShowDialog(this) == true) { ViewModel.LocalPath = dialog.FileName; await ViewModel.UploadCommand.ExecuteAsync(); }
    }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFile is not { } selected) { ViewModel.Message = "请先选择文件或文件夹"; return; }
        if (selected.IsDirectory)
        {
            var dialog = new OpenFolderDialog { Title = "选择导出文件夹的保存位置" };
            if (dialog.ShowDialog(this) == true) await ViewModel.DownloadAsync(dialog.FolderName);
        }
        else
        {
            var dialog = new SaveFileDialog { FileName = selected.Name };
            if (dialog.ShowDialog(this) == true) await ViewModel.DownloadAsync(dialog.FileName);
        }
    }
    private void BrowseSync_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择与当前安卓目录比较的本地文件夹" };
        if (dialog.ShowDialog(this) == true) ViewModel.LocalPath = dialog.FolderName;
    }
    private async void File_DoubleClick(object sender, MouseButtonEventArgs e) => await ViewModel.EnterSelectedFolderAsync();
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCheckpoint is null) return;
        if (MessageBox.Show(this, "恢复会停止安卓并切回所选检查点；当前磁盘保留为回退副本。继续？", "恢复检查点", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await ViewModel.RestoreSelectedAsync();
    }
    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsRunning || string.IsNullOrWhiteSpace(ViewModel.Package)) return;
        if (MessageBox.Show(this, "卸载 " + ViewModel.Package + " 会删除该应用和它的数据。继续？", "卸载应用", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await ViewModel.UninstallConfirmedAsync();
    }
    private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (RuntimeForm.BindingGroup?.CommitEdit() == false) { ViewModel.Message = "请先修正运行配置中的输入"; return; }
        await ViewModel.ApplyProfileCommand.ExecuteAsync();
    }
    private void Recommended_Click(object sender, RoutedEventArgs e)
    {
        var profile = RuntimeProfile.Recommended;
        ViewModel.Width = profile.Width; ViewModel.Height = profile.Height; ViewModel.Density = profile.Density;
        ViewModel.RefreshRate = profile.RefreshRate; ViewModel.MemoryMb = profile.MemoryMb; ViewModel.StartAvailableMb = profile.StartAvailableMb; ViewModel.LowRam = profile.LowRam; ViewModel.Cores = profile.CpuCores;
        ViewModel.SelectedRenderer = profile.Renderer; ViewModel.Vulkan = profile.Vulkan;
        ViewModel.DesktopDisplay = profile.DesktopDisplay;
        ViewModel.Message = "已填入推荐配置，点击保存后才会应用";
    }
    private async void Resources_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _previewTimer.Stop();
            if (!_offline) await new DebugClient().ExecuteAndWaitAsync(new("quiesce"));
            new StorageWindow { Owner = this }.ShowDialog();
            if (!_offline) await ViewModel.RefreshAsync();
        }
        catch (Exception error) { ViewModel.Message = error.Message; }
        finally { if (!_offline) _previewTimer.Start(); }
    }
    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "RootedAndroidGameVM.Setup.exe");
        if (!File.Exists(executable)) { ViewModel.Message = "找不到安装器，请运行完整安装包"; return; }
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }
    private void OpenRecords_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedWork is { Directory: null }) { ViewModel.Message = "所选任务没有生成记录目录。"; return; }
        var directory = ViewModel.SelectedWork?.Directory ?? ViewModel.RecordDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) { ViewModel.Message = "尚无记录目录"; return; }
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
    private void ShowText(string title, string text)
    {
        var output = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(15),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        new Window { Owner = this, Title = title, Width = 900, Height = 600, Content = output, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
    private void ShowResult_Click(object sender, RoutedEventArgs e) => ShowText("操作详细结果", ViewModel.SelectedWork?.Details ?? ViewModel.RawResult);
    private void OpenCliDocs_Click(object sender, RoutedEventArgs e) => ShowText("CLI 与 AI 调用", """
        RootedAndroidGameVM.Cli.exe status
        RootedAndroidGameVM.Cli.exe capabilities
        RootedAndroidGameVM.Cli.exe start --wait
        RootedAndroidGameVM.Cli.exe screen
        RootedAndroidGameVM.Cli.exe runtime.inspect --wait
        RootedAndroidGameVM.Cli.exe --request request.json --wait

        请求文件：
        {"schemaVersion":1,"command":"logs","arguments":{"package":"test.app","seconds":30}}

        长任务先返回 jobId，--wait 输出 NDJSON 并等待结果。input 必须保持查询，
        超过 5 秒不查询会取消并释放触点。窗口与 CLI 共用任务和实例校验。
        输入坐标以保存的原始 PNG 为准；分辨率、方向、应用或会话变化后请重新观察。
        APK 与应用文件从本机选择，不随产品分发。私钥、账号数据不应加入诊断材料。

        完整说明：仓库 docs/cli.md 与 docs/debug-workbench.md。
        """);
    private async void SixTouchTemplate_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CaptureAsync();
        if (ViewModel.Observation is not { } screen) return;
        var turn = (screen.Rotation - screen.ImageRotation + 360) % 360;
        var width = turn is 90 or 270 ? screen.Height : screen.Width;
        var height = turn is 90 or 270 ? screen.Width : screen.Height;
        var points = Enumerable.Range(0, 6).Select(i => AndroidDebugService.ToNativeTouch(new TouchPoint(i, width * (2 * i + 1) / 12, height * 4 / 5),
            screen with { Width = width, Height = height, ImageRotation = turn })).ToArray();
        var frames = new List<InputFrame> { new(0, points) };
        for (var i = 0; i < points.Length; i++) frames.Add(new(1000 + i * 150, [points[i] with { Pressure = 0 }]));
        ViewModel.TestJson = JsonSerializer.Serialize(DebugRequest.Create("input", new { observation = screen.Id, frames }), new JsonSerializerOptions(DebugJson.Options) { WriteIndented = true });
        ViewModel.Message = "模板已生成，请按实际目标位置调整坐标后运行";
    }
    private async void OpenTest_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "测试请求|*.json" };
        if (dialog.ShowDialog(this) == true)
        {
            if (new FileInfo(dialog.FileName).Length > 1024 * 1024) { ViewModel.Message = "请求文件超过 1 MiB"; return; }
            ViewModel.TestJson = await File.ReadAllTextAsync(dialog.FileName);
        }
    }
    private async void AndroidKey_Click(object sender, RoutedEventArgs e) => await ViewModel.KeyAsync(((Button)sender).Tag.ToString()!);
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
            ViewModel.Section == WorkstationSection.Applications && Path.GetExtension(files[0]).Equals(".apk", StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel.Section != WorkstationSection.Applications || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files) return;
        ViewModel.SelectedNavigation = ViewModel.Navigation.Single(item => item.Section == WorkstationSection.Applications);
        if (Path.GetExtension(files[0]).Equals(".apk", StringComparison.OrdinalIgnoreCase))
        { ViewModel.ApkPath = files[0]; await ViewModel.InspectApkAsync(); }
    }
}
