using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Ui.Workstation;
using RootedAndroidGameVM.Launcher;

namespace RootedAndroidGameVM.UiProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        JsonElement? catalogSnapshot = null;
        if (args.Length == 3 && args[0] == "--catalog")
        {
            using var catalog = JsonDocument.Parse(File.ReadAllText(args[1]));
            catalogSnapshot = catalog.RootElement.Clone();
            args = [args[2]];
        }
        if (args.Length == 2 && args[0] == "--preview")
        {
            var frame = new DebugClient().ReadPreviewAsync(CancellationToken.None).GetAwaiter().GetResult();
            var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "preview.png"), frame.Payload.Span);
            var segment = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.Payload, out var array) ? array : new ArraySegment<byte>(frame.Payload.ToArray());
            using var stream = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
            var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoded.Frames[0].PixelWidth != frame.Metadata.Width || decoded.Frames[0].PixelHeight != frame.Metadata.Height) return 1;
            File.WriteAllText(Path.Combine(output, "preview.json"), JsonSerializer.Serialize(frame.Metadata, DebugJson.Options));
            Console.WriteLine($"Binary preview decoded: {frame.Metadata.Width}x{frame.Metadata.Height}, {frame.Payload.Length} bytes.");
            return 0;
        }
        var sessionOnly = args.Length == 2 && args[0] is "--session" or "--live-session";
        JsonElement? liveSnapshot = null;
        if (sessionOnly && args[0] == "--live-session")
            liveSnapshot = new DebugClient().ExecuteAndWaitAsync(DebugRequest.Create("session.summary", new { refresh = true })).GetAwaiter().GetResult();
        if (sessionOnly) args = [args[1]];
        if (args.Length != 1) return 2;
        var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        var errors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var app = new Application();
        var window = new WorkstationWindow(liveSnapshot is { } snapshot ? new SnapshotApi(snapshot) : new SampleApi(catalogSnapshot), offline: true);
        var model = window.ViewModel;
        model.RefreshAsync().GetAwaiter().GetResult();
        if (!sessionOnly)
        {
            model.RefreshRuntimeAsync().GetAwaiter().GetResult();
            model.RefreshApplicationsAsync().GetAwaiter().GetResult();
            model.SelectedApplication = model.Applications.FirstOrDefault(); // Explicit fixture selection, not a product default.
            model.BrowseFilesAsync().GetAwaiter().GetResult();
            model.RefreshCheckpointsAsync().GetAwaiter().GetResult();
        }
        if (sessionOnly && window.FindName("SessionExpander") is System.Windows.Controls.Expander expanded) expanded.IsExpanded = true;
        File.WriteAllText(Path.Combine(root, "session-text.txt"), model.SessionText);
        if (liveSnapshot is { } live) File.WriteAllText(Path.Combine(root, "session-cli-snapshot.json"), live.GetRawText());
        model.LogText = "[app] 12:30:00 Unity 初始化完成\n[pid_change] 旧进程退出，开始跟随新 PID\n[lua] 本地测试消息\n[app] 资源加载完成";
        var content = (FrameworkElement)window.Content;
        var outputs = new List<object>();
        foreach (var size in new[] { new Size(1100, 720), new Size(1320, 860) })
        {
            foreach (var section in sessionOnly ? model.Navigation.Take(1) : model.Navigation)
            {
                model.SelectedNavigation = section;
                content.Width = size.Width; content.Height = size.Height;
                content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                var target = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                target.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                var path = Path.Combine(root, $"{section.Section}-{size.Width:0}.png");
                using var file = File.Create(path); encoder.Save(file);
                outputs.Add(new { section = section.Section.ToString(), width = size.Width, height = size.Height, path });
            }
        }
        File.WriteAllText(Path.Combine(root, "binding-errors.txt"), errors.ToString());
        File.WriteAllText(Path.Combine(root, "rendered.json"), JsonSerializer.Serialize(outputs, new JsonSerializerOptions { WriteIndented = true }));
        if (catalogSnapshot is not null)
            File.WriteAllText(Path.Combine(root, "catalog-bound.json"), JsonSerializer.Serialize(model.Applications.Select(row => new { row.Name, row.Package, row.IconPath }), DebugJson.Options));
        model.Dispose(); window.Close(); app.Shutdown();
        Console.WriteLine($"Rendered {outputs.Count} offscreen layouts; binding errors: {errors.GetStringBuilder().Length} characters.");
        return errors.GetStringBuilder().Length == 0 ? 0 : 1;
    }

    private sealed class SnapshotApi(JsonElement snapshot) : IWorkstationApi
    {
        public Task<PreviewFrame> PreviewAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JsonElement> ExecuteAsync(DebugRequest request, CancellationToken cancellationToken, Action<JsonElement>? progress = null) =>
            request.Command == "session.summary" ? Task.FromResult(snapshot) : throw new NotSupportedException("Live snapshot probe only renders the captured session.");
    }

    private sealed class SampleApi(JsonElement? catalog = null) : IWorkstationApi
    {
        public Task<PreviewFrame> PreviewAsync(CancellationToken cancellationToken) => throw new NotSupportedException("No live device in offscreen probe.");
        public Task<JsonElement> ExecuteAsync(DebugRequest request, CancellationToken cancellationToken, Action<JsonElement>? progress = null)
        {
            if (request.Command == "apps.list" && catalog is { } snapshot) return Task.FromResult(snapshot);
            object result = request.Command switch
            {
                "session.summary" => SampleSession(),
                "status" => new { status = "Running", serial = "emulator-5554", dataRoot = @"D:\Android-Data", root = true, state = new { awake = true, locked = false, foreground = "com.example.notes" } },
                "runtime.inspect" => new { requested = RuntimeProfile.Recommended, observed = new { activeRefreshRate = 120.0, renderer = "GLES: NVIDIA · 硬件渲染" }, host = new { totalMb = 16384, availableMb = 4800 } },
                "apps.list" => new
                {
                    entries = new[] {
                    new { package = "com.example.notes", name = "笔记", appRef = "notes-ref", userId = 0, system = false },
                    new { package = "com.example.reader", name = "文档阅读器", appRef = "reader-ref", userId = 0, system = false }
                }
                },
                "files.list" => new { entries = new[] { new { name = "documents", details = "directory|4096|10212|10212|700" }, new { name = "images", details = "directory|4096|10212|10212|700" }, new { name = "readme.txt", details = "regular file|1234|10212|10212|600" }, new { name = "中文文件名与较长的内容说明.json", details = "regular file|65536|10212|10212|600" } } },
                "checkpoint.list" => new[] { new { id = "20260913-120000-example", path = @"D:\Android-Data\checkpoints\20260913-120000-example" } },
                _ => new { success = true }
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(result, DebugJson.Options));
        }
        private static object SampleSession()
        {
            var at = DateTimeOffset.UtcNow;
            var summary = new SessionSummary(at, "emulator-5554", "Running", "1234:5678", "com.example.notes",
                new("com.example.notes", "4321", "com.example.notes", true, false, "activity_ready", at, @"D:\Android-Data\debug-runs\activity.txt"),
                null, [new("job-example", "request-example", "files.push", "transferring", "failed", "1234:5678", "4321", @"D:\Android-Data\debug-runs\import", null, "app_not_ready")],
                null,
                new("acknowledged", true, "1234:5678", at, [], null, @"D:\Android-Data\debug-runs\release.json"), [],
                @"D:\Android-Data\debug-runs\import", ["查看失败任务原始记录"], "核查传输结果", null, "");
            summary = summary with { Text = SessionSummaryText.Render(summary) };
            return new
            {
                runtime = new
                {
                    status = "Running",
                    serial = "emulator-5554",
                    dataRoot = @"D:\Android-Data",
                    root = true,
                    state = new { awake = true, locked = false, foreground = "com.example.notes" }
                },
                summary
            };
        }
    }
}
