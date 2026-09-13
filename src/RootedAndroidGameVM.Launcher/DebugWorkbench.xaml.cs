using Microsoft.Win32;
using TouchPoint = RootedAndroidGameVM.Core.Debugging.TouchPoint;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Setup;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace RootedAndroidGameVM.Launcher;

public partial class DebugWorkbench : Window
{
    private readonly DebugClient _client = new();
    private readonly HashSet<string> _jobs = new();
    private ScreenObservation? _screen;
    private string? _lastDirectory;
    private string? _currentJob;
    private string _logText = "";
    private string? _browsedDirectory;
    private sealed record FileRow(string Name, string Details);
    private (int X, int Y)? _down;
    private Stopwatch? _gesture;
    public DebugWorkbench()
    {
        InitializeComponent();
        Closed += async (_, _) => { foreach (var id in _jobs.ToArray()) await _client.SendAsync(DebugRequest.Create("cancel", new { id })); };
    }
    private object Args() => new { package = PackageText.Text.Trim(), scope = ((ComboBoxItem)ScopeBox.SelectedItem).Tag.ToString(), remote = RemoteText.Text.Trim() };
    private async Task<JsonElement?> RunAsync(DebugRequest request)
    {
        try
        {
            StatusText.Text = "执行：" + request.Command;
            var reply = await _client.SendAsync(request);
            if (!reply.Ok) throw new IOException(reply.Error?.Message);
            var data = (JsonElement)reply.Result!;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("jobId", out var job))
            {
                var id = job.GetString()!; _jobs.Add(id); _currentJob = id;
                try
                {
                    while (true)
                    {
                        await Task.Delay(500);
                        reply = await _client.SendAsync(DebugRequest.Create("job", new { id }));
                        if (!reply.Ok) throw new IOException(reply.Error?.Message);
                        var state = (JsonElement)reply.Result!;
                        if (state.TryGetProperty("progress", out var progress) && progress.TryGetProperty("directory", out var path))
                        {
                            _lastDirectory = path.GetString();
                            var log = Path.Combine(_lastDirectory!, "events.ndjson");
                            if (File.Exists(log))
                            {
                                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                using var reader = new StreamReader(stream);
                                _logText = await reader.ReadToEndAsync(); ApplyLogFilter();
                            }
                        }
                        StatusText.Text = request.Command + " · " + state.GetProperty("status").GetString() + " · " + id;
                        if (!state.GetProperty("completed").GetBoolean()) continue;
                        var completed = state.GetProperty("result");
                        if (!completed.GetProperty("ok").GetBoolean()) throw new IOException(completed.GetProperty("error").GetProperty("message").GetString());
                        data = completed.GetProperty("result"); break;
                    }
                }
                finally { _jobs.Remove(id); if (_currentJob == id) _currentJob = null; }
            }
            var text = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            FileOutput.Text = text; ShellOutput.Text = text;
            if (request.Command == "apps") AppsBox.ItemsSource = data.EnumerateArray().Select(v => v.GetString()).ToArray();
            if (request.Command == "files.list")
            {
                _browsedDirectory = RemoteText.Text;
                FileGrid.ItemsSource = data.GetProperty("entries").EnumerateArray().Select(v => new FileRow(v.GetProperty("name").GetString()!, v.GetProperty("details").GetString()!)).ToArray();
            }
            if (request.Command == "metrics") { _logText = text; ApplyLogFilter(); }
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("directory", out var directory)) _lastDirectory = directory.GetString();
                if (request.Command == "screen") SetScreen(data);
                else if (data.TryGetProperty("after", out var after)) SetScreen(after);
                else if (data.TryGetProperty("screen", out var screenshot)) SetScreen(screenshot);
            }
            StatusText.Text = "完成：" + request.Command; return data;
        }
        catch (Exception e) { StatusText.Text = "未完成：" + e.Message; return null; }
    }
    private void SetScreen(JsonElement data)
    {
        _screen = data.Deserialize<ScreenObservation>(DebugJson.Options)!;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(_screen.Path); bitmap.EndInit(); bitmap.Freeze();
        Preview.Source = bitmap;
        Preview.LayoutTransform = new System.Windows.Media.RotateTransform(_screen.Backend == "emulator-grpc" ? _screen.ImageRotation - _screen.Rotation : 0);
        ScreenInfo.Text = $"{_screen.Width} × {_screen.Height} · 旋转 {_screen.Rotation}°\n{_screen.Backend}\n{_screen.Foreground}\n{(_screen.Awake ? "亮屏" : "息屏")} · {(_screen.Locked ? "锁屏" : "未锁定")}";
        _lastDirectory = Path.GetDirectoryName(_screen.Path);
    }
    private async void Command_Click(object sender, RoutedEventArgs e) => await RunAsync(DebugRequest.Create(((Button)sender).Tag.ToString()!, Args()));
    private async void Key_Click(object sender, RoutedEventArgs e) => await RunAsync(DebugRequest.Create("key", new { key = ((Button)sender).Tag.ToString() }));
    private async void SetClipboard_Click(object sender, RoutedEventArgs e) => await RunAsync(DebugRequest.Create("clipboard", new { text = ClipboardText.Text }));
    private async void GetClipboard_Click(object sender, RoutedEventArgs e) { var data = await RunAsync(new("clipboard")); if (data is { } d) ClipboardText.Text = d.GetProperty("text").GetString(); }
    private async void Cancel_Click(object sender, RoutedEventArgs e) { if (_currentJob is not null) await _client.SendAsync(DebugRequest.Create("cancel", new { id = _currentJob })); }
    private void OpenRecords_Click(object sender, RoutedEventArgs e)
    {
        var directory = _lastDirectory ?? Path.Combine(InstallPaths.CreateDefault().ProductRoot, "debug-runs");
        Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
    private (int X, int Y)? Pixel(Point point)
    {
        if (_screen is null) return null;
        var scale = Math.Min(Preview.ActualWidth / _screen.Width, Preview.ActualHeight / _screen.Height);
        var x = (point.X - (Preview.ActualWidth - _screen.Width * scale) / 2) / scale;
        var y = (point.Y - (Preview.ActualHeight - _screen.Height * scale) / 2) / scale;
        return x < 0 || y < 0 || x >= _screen.Width || y >= _screen.Height ? null : ((int)x, (int)y);
    }
    private void Preview_MouseMove(object sender, MouseEventArgs e) { var p = Pixel(e.GetPosition(Preview)); CoordinateText.Text = p is { } v ? $"原始像素：{v.X}, {v.Y}（松开鼠标后发送手势）" : "图像以外区域"; }
    private void Preview_Down(object sender, MouseButtonEventArgs e) { _down = Pixel(e.GetPosition(Preview)); if (_down is not null) { _gesture = Stopwatch.StartNew(); Preview.CaptureMouse(); } }
    private async void Preview_Up(object sender, MouseButtonEventArgs e)
    {
        var end = Pixel(e.GetPosition(Preview)); Preview.ReleaseMouseCapture();
        if (_down is not { } start || end is not { } finish || _screen is null || _gesture is null) return;
        var ms = Math.Clamp((int)_gesture.ElapsedMilliseconds, 40, 10000); _down = null;
        var frames = new List<InputFrame>();
        var moving = Math.Abs(start.X - finish.X) + Math.Abs(start.Y - finish.Y) > 10;
        var count = moving ? Math.Max(2, ms / 25) : 1;
        for (var i = 0; i < count; i++) { var t = (double)i / count; frames.Add(new((int)(t * ms), [new(0, (int)(start.X + (finish.X - start.X) * t), (int)(start.Y + (finish.Y - start.Y) * t))])); }
        frames.Add(new(ms, [new(0, finish.X, finish.Y, 0)]));
        await RunAsync(DebugRequest.Create("input", new { observation = _screen.Id, frames }));
    }
    private async void Install_Click(object sender, RoutedEventArgs e) => await PickAsync("install", "APK|*.apk");
    private async void Import_Click(object sender, RoutedEventArgs e) => await PickAsync("malody.import", "Malody 内容|*.msp;*.mcz");
    private async void Reload_Click(object sender, RoutedEventArgs e) => await PickAsync("malody.reload", "Malody 内容|*.msp;*.mcz");
    private async Task PickAsync(string command, string filter) { var dialog = new OpenFileDialog { Filter = filter }; if (dialog.ShowDialog(this) == true) await RunAsync(DebugRequest.Create(command, new { path = dialog.FileName })); }
    private async void FileCommand_Click(object sender, RoutedEventArgs e) => await RunAsync(DebugRequest.Create("files.list", Args()));
    private DebugRequest FileRequest(string command, string local) => DebugRequest.Create(command, new { local, package = PackageText.Text.Trim(), scope = ((ComboBoxItem)ScopeBox.SelectedItem).Tag.ToString(), remote = RemoteText.Text.Trim() });
    private async void Upload_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog(); if (dialog.ShowDialog(this) == true) { if (RemoteText.Text == _browsedDirectory || RemoteText.Text.EndsWith('/')) RemoteText.Text = RemoteText.Text.TrimEnd('/') + "/" + Path.GetFileName(dialog.FileName); await RunAsync(FileRequest("files.push", dialog.FileName)); } }
    private void Apps_Changed(object sender, SelectionChangedEventArgs e) { if (AppsBox.SelectedItem is string package) PackageText.Text = package; }
    private async void FileGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileGrid.SelectedItem is not FileRow row || _browsedDirectory is null) return;
        RemoteText.Text = (_browsedDirectory.TrimEnd('/') + "/" + row.Name).TrimStart('/');
        if (row.Details.StartsWith("directory", StringComparison.Ordinal)) await RunAsync(DebugRequest.Create("files.list", Args()));
    }
    private async void Download_Click(object sender, RoutedEventArgs e) { var dialog = new SaveFileDialog(); if (dialog.ShowDialog(this) == true) await RunAsync(FileRequest("files.pull", dialog.FileName)); }
    private async void Folder_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) await RunAsync(FileRequest(((Button)sender).Tag.ToString()!, dialog.FolderName)); }
    private async void Diagnostic_Click(object sender, RoutedEventArgs e) => await RunAsync(DebugRequest.Create(((Button)sender).Tag.ToString()!, new { package = PackageText.Text.Trim(), seconds = int.TryParse(SecondsText.Text, out var n) ? n : 30 }));
    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyLogFilter();
    private void ApplyLogFilter() { if (LogOutput is null) return; var filter = LogFilter.Text; LogOutput.Text = string.Join('\n', _logText.Split('\n').Where(l => filter.Length == 0 || l.Contains(filter, StringComparison.OrdinalIgnoreCase)).TakeLast(5000)); LogOutput.ScrollToEnd(); }
    private void Errors_Click(object sender, RoutedEventArgs e) { LogFilter.Text = ""; LogOutput.Text = string.Join('\n', _logText.Split('\n').Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, "exception|error|ANR|crash|fatal|lua", System.Text.RegularExpressions.RegexOptions.IgnoreCase))); }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "恢复会停止虚拟机并切回所选检查点。当前磁盘会另存为回退副本。继续？", "恢复检查点", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            await RunAsync(DebugRequest.Create("checkpoint.restore", new { id = CheckpointId.Text.Trim() }));
    }
    private void Template_Click(object sender, RoutedEventArgs e)
    {
        if (_screen is null) { StatusText.Text = "先到屏幕页获取截图。"; return; }
        var turn = (_screen.Rotation - _screen.ImageRotation + 360) % 360;
        var width = turn is 90 or 270 ? _screen.Height : _screen.Width;
        var height = turn is 90 or 270 ? _screen.Width : _screen.Height;
        var points = Enumerable.Range(0, 6).Select(i => AndroidDebugService.ToNativeTouch(new TouchPoint(i, width * (2 * i + 1) / 12, height * 4 / 5),
            _screen with { Width = width, Height = height, ImageRotation = turn })).ToArray();
        var frames = new List<InputFrame> { new(0, points) };
        for (var i = 0; i < 6; i++) frames.Add(new(500 + i * 150, [points[i] with { Pressure = 0 }]));
        TestEditor.Text = JsonSerializer.Serialize(DebugRequest.Create("input", new { observation = _screen.Id, frames }), new JsonSerializerOptions(DebugJson.Options) { WriteIndented = true });
        StatusText.Text = "请按截图调整六个触点坐标；模板同时按下，再依次松开。";
    }
    private async void OpenTest_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "测试 JSON|*.json" }; if (dialog.ShowDialog(this) == true) TestEditor.Text = await File.ReadAllTextAsync(dialog.FileName); }
    private async void RunTest_Click(object sender, RoutedEventArgs e) { try { await RunAsync(JsonSerializer.Deserialize<DebugRequest>(TestEditor.Text, DebugJson.Options)!); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private async void Shell_Click(object sender, RoutedEventArgs e) => await RunAsync(DebugRequest.Create(((Button)sender).Tag.ToString()!, new { script = ShellEditor.Text }));
}
