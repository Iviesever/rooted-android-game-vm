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
using RootedAndroidGameVM.Launcher.Workstation;

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
        var filesOnly = args.Length == 2 && args[0] == "--files";
        if (filesOnly) args = [args[1]];
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
            model.FileWorkspace.RequestedApplication = model.SelectedApplication;
            model.FileWorkspace.InitializeAsync().GetAwaiter().GetResult();
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
            foreach (var section in sessionOnly ? model.Navigation.Take(1) : filesOnly ? model.Navigation.Where(item => item.Section == WorkstationSection.Files) : model.Navigation)
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
        if (filesOnly)
        {
            var review = new TransferReviewWindow(new TransferReviewModel(JsonSerializer.SerializeToElement(new
            {
                planId = "preview-plan",
                direction = "download",
                totalEntries = 3,
                totalBytes = 123456,
                counts = new { @new = 1, different = 1, merge = 1 },
                issues = Array.Empty<string>(),
                applicationsToStop = new[] { new TransferApplication("com.example.notes", 0, "revision", true) },
                requiresConflictPolicy = true,
                conflicts = new[] { new TransferPlanEntry(0, "source", "documents/笔记.txt", "documents/笔记.txt",
                    new TransferFingerprint("file", 123456, "v", "hash"), null, "different") },
                preview = Array.Empty<TransferPlanEntry>()
            }, DebugJson.Options), @"D:\导出数据\笔记"));
            var reviewContent = (FrameworkElement)review.Content; var reviewSize = new Size(840, 600);
            reviewContent.Width = reviewSize.Width - 48; reviewContent.Height = reviewSize.Height - 48;
            reviewContent.Measure(reviewSize); reviewContent.Arrange(new Rect(reviewSize)); reviewContent.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var grid = System.Windows.LogicalTreeHelper.GetChildren(reviewContent).OfType<System.Windows.Controls.DataGrid>().Single();
            if (grid.Columns[0].ActualWidth < 320) throw new InvalidOperationException("Transfer target column is clipped.");
            File.WriteAllText(Path.Combine(root, "review-column-widths.json"), JsonSerializer.Serialize(grid.Columns.Select(column => column.ActualWidth)));
            var bitmap = new RenderTargetBitmap(840, 600, 96, 96, PixelFormats.Pbgra32); bitmap.Render(reviewContent);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(root, "TransferReview.png"))) png.Save(output);
            review.Close();
        }
        if (model.HasError) errors.WriteLine(model.Error);
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
                "users.list" => new { users = new[] { new AndroidUser(0, "所有者", true), new AndroidUser(10, "访客", false) } },
                "files.roots" => new
                {
                    roots = new[] {
                    new FileRootDescriptor("private-ref", "private", "私有数据", "/data/user/0/com.example.notes", true, true, true, false, null, "private-entry"),
                    new FileRootDescriptor("external-ref", "external", "外部应用数据", "/storage/emulated/0/Android/data/com.example.notes", true, true, true, false, null, "external-entry"),
                    new FileRootDescriptor("obb-ref", "obb", "扩展数据", "/storage/emulated/0/Android/obb/com.example.notes", false, false, false, false, "not_created", null),
                    new FileRootDescriptor("shared-ref", "shared", "共享存储", "/storage/emulated/0", true, true, true, false, null, "shared-entry") }
                },
                "files.browse" => new FileBrowsePage("private-ref", "", SampleFile("", "directory"),
                    [SampleFile("documents", "directory"), SampleFile("images", "directory"), SampleFile("readme.txt", "file"), SampleFile("中文笔记与较长的内容说明.json", "file")], 210, "next-page", "snapshot", DateTimeOffset.UtcNow),
                "files.transfer.list" => new { transfers = new[] { new FileTransferHistoryRow("sample-plan", "download", "cancelled", DateTimeOffset.UtcNow, 210, 123456, @"D:\Android-Data\debug-runs\transfers\sample-plan", true) } },
                "files.list" => new { entries = new[] { new { name = "documents", details = "directory|4096|10212|10212|700" }, new { name = "images", details = "directory|4096|10212|10212|700" }, new { name = "readme.txt", details = "regular file|1234|10212|10212|600" }, new { name = "中文文件名与较长的内容说明.json", details = "regular file|65536|10212|10212|600" } } },
                "checkpoint.list" => new[] { new { id = "20260913-120000-example", path = @"D:\Android-Data\checkpoints\20260913-120000-example" } },
                _ => new { success = true }
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(result, DebugJson.Options));
        }
        private static RemoteFileEntry SampleFile(string name, string kind) => new(name, name, kind, 123456, 1789890000000, 10123, 10123, kind == "directory" ? "700" : "600", "version", EntryRef: "ref-" + name);
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
