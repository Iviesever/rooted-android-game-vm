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
        var window = new WorkstationWindow(liveSnapshot is { } snapshot ? new SnapshotApi(snapshot) : new SampleApi(), offline: true);
        var model = window.ViewModel;
        model.RefreshAsync().GetAwaiter().GetResult();
        if (!sessionOnly)
        {
            model.RefreshRuntimeAsync().GetAwaiter().GetResult();
            model.RefreshApplicationsAsync().GetAwaiter().GetResult();
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

    private sealed class SampleApi : IWorkstationApi
    {
        public Task<PreviewFrame> PreviewAsync(CancellationToken cancellationToken) => throw new NotSupportedException("No live device in offscreen probe.");
        public Task<JsonElement> ExecuteAsync(DebugRequest request, CancellationToken cancellationToken, Action<JsonElement>? progress = null)
        {
            object result = request.Command switch
            {
                "session.summary" => SampleSession(),
                "status" => new { status = "Running", serial = "emulator-5554", dataRoot = @"D:\Android-Data", root = true, state = new { awake = true, locked = false, foreground = "me.mugzone.emiria" } },
                "runtime.inspect" => new { requested = RuntimeProfile.Recommended, observed = new { activeRefreshRate = 120.0, renderer = "GLES: NVIDIA · 硬件渲染" }, host = new { totalMb = 16384, availableMb = 4800 } },
                "apps" => new[] { "me.mugzone.emiria", "com.example.toolbox", "com.example.test" },
                "files.list" => new { entries = new[] { new { name = "skin", details = "directory|4096|10212|10212|700" }, new { name = "chart", details = "directory|4096|10212|10212|700" }, new { name = "readme.txt", details = "regular file|1234|10212|10212|600" }, new { name = "中文文件名与较长的内容说明.json", details = "regular file|65536|10212|10212|600" } } },
                "checkpoint.list" => new[] { new { id = "20260913-120000-example", path = @"D:\Android-Data\checkpoints\20260913-120000-example" } },
                _ => new { success = true }
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(result, DebugJson.Options));
        }
        private static object SampleSession()
        {
            var at = DateTimeOffset.UtcNow;
            var summary = new SessionSummary(at, "emulator-5554", "Running", "1234:5678", "me.mugzone.emiria",
                new("me.mugzone.emiria", "4321", "me.mugzone.emiria", true, false, "activity_ready", at, @"D:\Android-Data\debug-runs\activity.txt"),
                null, [new("job-example", "request-example", "malody.import", "waiting_for_app", "failed", "1234:5678", "4321", @"D:\Android-Data\debug-runs\import", null, "app_not_ready")],
                new("import-example", "failed", false, 0, 5, 0, "app_not_ready", at, "1234:5678", @"D:\Android-Data\debug-runs\import", @"D:\Android-Data\debug-runs\import\import.json"),
                new("acknowledged", true, "1234:5678", at, [], null, @"D:\Android-Data\debug-runs\release.json"), [],
                @"D:\Android-Data\debug-runs\import", ["原importId可继续，不重复上传"], "继续原导入并核验", DebugRequest.Create("malody.import", new { importId = "import-example" }), "");
            summary = summary with { Text = SessionSummaryText.Render(summary) };
            return new { runtime = new { status = "Running", serial = "emulator-5554", dataRoot = @"D:\Android-Data", root = true,
                state = new { awake = true, locked = false, foreground = "me.mugzone.emiria" } }, summary };
        }
    }
}
