using System.Collections.ObjectModel;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Ui.Workstation;

public sealed class WorkstationViewModel : ObservableState, IDisposable
{
    private readonly IWorkstationApi _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<AsyncAction> _commands = [];
    private int _disposed;
    private WorkstationNavigation _selectedNavigation;
    private bool _running, _busy, _refreshing;
    private string _status = "正在连接", _statusDetail = "读取当前虚拟机状态", _message = "准备就绪", _error = "";
    private string _dataRoot = "", _serial = "", _rootStatus = "等待启动", _display = "等待检测", _renderer = "等待检测", _hostMemory = "等待检测";
    private string _package = "", _apkPath = "", _apkSummary = "选择本机 APK 后会检查版本和架构";
    private string _remoteFolder = "", _localPath = "", _rawResult = "", _logText = "", _logFilter = "", _script = "getprop ro.product.cpu.abilist";
    private string _testJson = "{\n  \"command\": \"screen\"\n}", _recordDirectory = "", _clipboard = "";
    private int _seconds = 30, _width = 1920, _height = 1080, _density = 240, _refreshRate = 120, _memoryMb = 3072, _cores = 4;
    private string _selectedRenderer = "host";
    private bool _vulkan, _previewEnabled, _desktopDisplay = true;
    private ApplicationRow? _selectedApplication;
    private string _applicationFilter = "";
    private bool _includeSystemApplications;
    private FileRow? _selectedFile;
    private CheckpointRow? _selectedCheckpoint;
    private WorkItem? _selectedWork;
    private FileScope _scope;
    private ScreenObservation? _observation;
    private PreviewFrame? _preview;
    private SessionSummary? _session;
    private string _sessionText = "会话尚未读取。", _sessionHeadline = "会话摘要 · 等待连接", _sessionArtifactDirectory = "";

    public WorkstationViewModel(IWorkstationApi api)
    {
        _api = api;
        FileWorkspace = new(RunAsync);
        Navigation = [
            new(WorkstationSection.Android, "使用安卓", "\uE7F8", "打开安卓窗口，或在这里观察与定位操作"),
            new(WorkstationSection.Applications, "应用管理", "\uE71D", "安装、打开和管理安卓应用"),
            new(WorkstationSection.Files, "文件管理", "\uE8B7", "选择应用与目录，双向传输文件和文件夹"),
            new(WorkstationSection.Diagnostics, "诊断与记录", "\uE9D9", "持续日志、异常、录像和性能依据"),
            new(WorkstationSection.Automation, "AI 与自动化", "\uE943", "可复现的输入、测试步骤和 Shell 调试"),
            new(WorkstationSection.Checkpoints, "检查点", "\uE81C", "保存与恢复当前安卓环境"),
            new(WorkstationSection.Settings, "运行设置", "\uE713", "显示、图形、内存和处理器配置")];
        _selectedNavigation = Navigation[0];
        Scopes = [new("external", "应用文件", "/sdcard/Android/data/<包名>"), new("private", "私有数据 · Root", "/data/data/<包名>"), new("shared", "共享下载", "/sdcard/Download")];
        _scope = Scopes[0];
        RefreshCommand = Action(RefreshAsync);
        RefreshSessionCommand = Action(async () => await RefreshSessionAsync(true));
        CancelSessionTaskCommand = Action(async () =>
        {
            var task = _session?.Tasks.FirstOrDefault(task => task.Status is "queued" or "running" or "cancelling");
            if (task is null) return;
            await RunAsync("取消会话任务", DebugRequest.Create("cancel", new { id = task.JobId })); await RefreshAsync();
        }, () => _session?.Tasks.Any(task => task.Status is "queued" or "running" or "cancelling") == true);
        StartCommand = Action(async () => { await RunAsync("启动安卓", new("start"), true); await RefreshAsync(); }, () => !IsRunning && !IsBusy && _session?.Status is not ("OperationInProgress" or "Unreachable"));
        StopCommand = Action(async () => { await RunAsync("保存并停止安卓", new("stop"), true); await RefreshAsync(); }, () => IsRunning && !IsBusy);
        OpenAndroidCommand = Action(async () => { if (!IsRunning && _session?.Status != "Unreachable" && await RunAsync("启动安卓", new("start"), true) is null) return; await RunAsync("打开安卓窗口", new("window.focus")); await RefreshAsync(); }, () => !IsBusy && _session?.Status != "OperationInProgress");
        CaptureCommand = Action(CaptureAsync, () => IsRunning);
        WakeCommand = Action(async () => { await RunAsync("唤醒", new("wake")); await RefreshAsync(); }, () => IsRunning);
        ReleaseCommand = Action(async () => await RunAsync("释放触点", new("release")), () => IsRunning);
        RefreshApplicationsCommand = Action(RefreshApplicationsAsync, () => IsRunning);
        InspectApkCommand = Action(InspectApkAsync, () => File.Exists(ApkPath));
        InstallCommand = Action(async () => { if (await RunAsync("安装应用", DebugRequest.Create("install", new { path = ApkPath }), true) is not null) await RefreshApplicationsAsync(); }, () => IsRunning && !IsBusy && File.Exists(ApkPath));
        LaunchCommand = Action(async () => await RunAsync("打开应用", DebugRequest.Create("launch", new { package = Package })), () => IsRunning && HasApplication && !IsBusy);
        StopApplicationCommand = Action(async () => await RunAsync("停止应用", DebugRequest.Create("force-stop", new { package = Package })), () => IsRunning && HasApplication && !IsBusy);
        ManageFilesCommand = Action(() => { FileWorkspace.RequestedApplication = SelectedApplication; SelectedNavigation = Navigation.Single(item => item.Section == WorkstationSection.Files); return Task.CompletedTask; }, () => IsRunning && HasApplication);
        BrowseFilesCommand = Action(BrowseFilesAsync, () => CanBrowseFiles);
        ParentFolderCommand = Action(async () => { RemoteFolder = RemoteFolder.Contains('/') ? RemoteFolder[..RemoteFolder.LastIndexOf('/')] : ""; await BrowseFilesAsync(); }, () => CanBrowseFiles && RemoteFolder.Length > 0);
        UploadCommand = Action(async () => { if (await RunAsync("上传文件", FileRequest("files.push", JoinRemote(Path.GetFileName(LocalPath)), LocalPath), true) is not null) await BrowseFilesAsync(); }, () => CanBrowseFiles && !IsBusy && File.Exists(LocalPath));
        CompareCommand = Action(async () => await RunAsync("比较目录", FileRequest("files.diff", RemoteFolder, LocalPath)), () => CanBrowseFiles && Directory.Exists(LocalPath));
        SyncCommand = Action(async () => { if (await RunAsync("同步目录", FileRequest("files.sync", RemoteFolder, LocalPath), true) is not null) await BrowseFilesAsync(); }, () => CanBrowseFiles && !IsBusy && Directory.Exists(LocalPath));
        LogsCommand = Action(async () => await RunAsync("应用日志", DebugRequest.Create("logs", new { package = Package, seconds = Seconds })), () => IsRunning && HasApplication);
        MetricsCommand = Action(async () =>
        {
            var result = await RunAsync("性能采样", DebugRequest.Create("metrics", new { package = Package }));
            if (result is not { } value) return;
            var memory = value.TryGetProperty("totalPssKb", out var pss) && pss.ValueKind == JsonValueKind.Number ? $"{pss.GetInt64() / 1024d:0.0} MiB" : "不可用";
            var frameTiming = value.TryGetProperty("frameTimingAvailable", out var available) && available.GetBoolean() ? "可用（详见完整结果）" : "不可用，不能按零耗时计算";
            LogText = $"应用：{Package}\n进程：{Text(value, "pid", "不可用")}\n应用 PSS：{memory}\nCPU：{Text(value, "cpu", "不可用")}\n帧时序：{frameTiming}\n\n完整原始依据可从“任务与结果”查看。";
        }, () => IsRunning && HasApplication);
        RecordCommand = Action(async () => await RunAsync("录像 · 无音频", DebugRequest.Create("record", new { seconds = Seconds })), () => IsRunning);
        FrameSampleCommand = Action(async () =>
        {
            var result = await RunAsync("呈现帧时序", DebugRequest.Create("frames.sample", new { package = Package, seconds = Math.Min(Seconds, 120) }));
            if (result is not { } value || !value.TryGetProperty("measurement", out var measurement) || !measurement.GetProperty("available").GetBoolean())
            { LogText = "当前应用没有可获取的 SurfaceView 呈现数据。"; return; }
            var summary = measurement.GetProperty("summary");
            LogText = $"应用实际呈现：{summary.GetProperty("observedFps").GetDouble():0.0} FPS\n帧间隔 P50：{summary.GetProperty("p50Ms").GetDouble():0.00} ms\nP95：{summary.GetProperty("p95Ms").GetDouble():0.00} ms\nP99：{summary.GetProperty("p99Ms").GetDouble():0.00} ms\n最长间隔：{summary.GetProperty("maxMs").GetDouble():0.00} ms\n样本帧数：{summary.GetProperty("frames").GetInt32()}\n\n来源为 SurfaceFlinger 呈现时间；有限缓冲可能缺样，不能用显示模式刷新率替代此数值。";
        }, () => IsRunning && HasApplication);
        TraceCommand = Action(async () => await RunAsync("系统追踪", DebugRequest.Create("trace", new { seconds = Seconds })), () => IsRunning);
        CancelCommand = Action(async () => { if (SelectedWork?.Id is { } id) await RunAsync("取消任务", DebugRequest.Create("cancel", new { id })); }, () => SelectedWork?.Id is not null && !SelectedWork.Completed);
        RefreshCheckpointsCommand = Action(RefreshCheckpointsAsync);
        CreateCheckpointCommand = Action(async () => { await RunAsync("停机保存检查点", new("checkpoint.create"), true); await RefreshCheckpointsAsync(); await RefreshAsync(); }, () => !IsBusy);
        RecoverCommand = Action(async () => { await RunAsync("恢复中断操作", new("checkpoint.recover"), true); await RefreshAsync(); }, () => !IsBusy);
        ApplyProfileCommand = Action(ApplyProfileAsync, () => !IsBusy);
        RefreshRuntimeCommand = Action(RefreshRuntimeAsync);
        ShellCommand = Action(async () => await RunAsync("普通 Shell", DebugRequest.Create("shell", new { script = Script, timeoutSeconds = 120 })), () => IsRunning && !IsBusy);
        RootShellCommand = Action(async () => await RunAsync("Root Shell", DebugRequest.Create("root-shell", new { script = Script, timeoutSeconds = 120 })), () => IsRunning && !IsBusy);
        RunTestCommand = Action(async () => { var request = JsonSerializer.Deserialize<DebugRequest>(TestJson, DebugJson.Options) ?? throw new ArgumentException("测试请求为空。"); await RunAsync("自动化请求", request, true); }, () => IsRunning && !IsBusy);
        ClipboardReadCommand = Action(async () => { var result = await _api.ExecuteAsync(new("clipboard"), _lifetime.Token); Clipboard = result.GetProperty("text").GetString() ?? ""; }, () => IsRunning);
        ClipboardWriteCommand = Action(async () => { await _api.ExecuteAsync(DebugRequest.Create("clipboard", new { text = Clipboard }), _lifetime.Token); Message = "已写入安卓剪贴板"; }, () => IsRunning);
    }

    public IReadOnlyList<WorkstationNavigation> Navigation { get; }
    public FileWorkspaceViewModel FileWorkspace { get; }
    public IReadOnlyList<FileScope> Scopes { get; }
    public IReadOnlyList<RendererChoice> Renderers { get; } = [new("host", "硬件加速 · Host"), new("software", "软件渲染 · 自动"), new("swiftshader", "软件渲染 · SwiftShader")];
    public IReadOnlyList<int> RefreshRates { get; } = [60, 90, 120];
    public ObservableCollection<ApplicationRow> Applications { get; } = [];
    public ObservableCollection<ApplicationRow> FilteredApplications { get; } = [];
    public string ApplicationFilter
    {
        get => _applicationFilter;
        set { if (Set(ref _applicationFilter, value)) { SelectedApplication = null; FilterApplications(); } }
    }
    public bool IncludeSystemApplications { get => _includeSystemApplications; set => Set(ref _includeSystemApplications, value); }
    public ObservableCollection<FileRow> Files { get; } = [];
    public ObservableCollection<CheckpointRow> Checkpoints { get; } = [];
    public ObservableCollection<WorkItem> Work { get; } = [];
    public WorkstationNavigation SelectedNavigation { get => _selectedNavigation; set { if (Set(ref _selectedNavigation, value)) { Changed(nameof(Section)); Changed(nameof(SectionTitle)); Changed(nameof(SectionDescription)); } } }
    public WorkstationSection Section => SelectedNavigation.Section;
    public string SectionTitle => SelectedNavigation.Title;
    public string SectionDescription => SelectedNavigation.Description;
    public bool IsRunning { get => _running; private set { if (Set(ref _running, value)) RefreshCommands(); } }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RefreshCommands(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public string SessionText { get => _sessionText; private set => Set(ref _sessionText, value); }
    public string SessionHeadline { get => _sessionHeadline; private set => Set(ref _sessionHeadline, value); }
    public string SessionArtifactDirectory { get => _sessionArtifactDirectory; private set => Set(ref _sessionArtifactDirectory, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public string Error { get => _error; private set { Set(ref _error, value.Length > 500 ? value[..500] + "…" : value); Changed(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string DataRoot { get => _dataRoot; private set => Set(ref _dataRoot, value); }
    public string Serial { get => _serial; private set => Set(ref _serial, value); }
    public string RootStatus { get => _rootStatus; private set => Set(ref _rootStatus, value); }
    public string Display { get => _display; private set => Set(ref _display, value); }
    public string Renderer { get => _renderer; private set => Set(ref _renderer, value); }
    public string HostMemory { get => _hostMemory; private set => Set(ref _hostMemory, value); }
    public string Package { get => _package; set { Set(ref _package, value); Changed(nameof(HasApplication)); RefreshCommands(); } }
    public bool HasApplication => !string.IsNullOrWhiteSpace(Package);
    public bool CanBrowseFiles => IsRunning && (Scope.Value == "shared" || HasApplication);
    public ApplicationRow? SelectedApplication
    {
        get => _selectedApplication;
        set
        {
            var targetChanged = Package != (value?.Package ?? "") || _selectedApplication?.AppRef != value?.AppRef;
            Set(ref _selectedApplication, value);
            Package = value?.Package ?? "";
            if (targetChanged) { RemoteFolder = ""; Files.Clear(); SelectedFile = null; }
        }
    }
    public string ApkPath { get => _apkPath; set { Set(ref _apkPath, value); RefreshCommands(); } }
    public string ApkSummary { get => _apkSummary; private set => Set(ref _apkSummary, value); }
    public FileScope Scope { get => _scope; set { if (Set(ref _scope, value)) { RemoteFolder = ""; Files.Clear(); SelectedFile = null; Changed(nameof(PrivateScope)); RefreshCommands(); } } }
    public bool PrivateScope => Scope.Value == "private";
    public string RemoteFolder { get => _remoteFolder; set { Set(ref _remoteFolder, value); RefreshCommands(); } }
    public string LocalPath { get => _localPath; set { Set(ref _localPath, value); RefreshCommands(); } }
    public FileRow? SelectedFile { get => _selectedFile; set => Set(ref _selectedFile, value); }
    public CheckpointRow? SelectedCheckpoint { get => _selectedCheckpoint; set => Set(ref _selectedCheckpoint, value); }
    public WorkItem? SelectedWork { get => _selectedWork; set { Set(ref _selectedWork, value); RefreshCommands(); } }
    public string RawResult { get => _rawResult; private set => Set(ref _rawResult, value); }
    public string LogText { get => _logText; set { Set(ref _logText, value); Changed(nameof(FilteredLogs)); } }
    public string LogFilter { get => _logFilter; set { Set(ref _logFilter, value); Changed(nameof(FilteredLogs)); } }
    public string FilteredLogs => string.Join('\n', LogText.Split('\n').Where(line => LogFilter.Length == 0 || line.Contains(LogFilter, StringComparison.OrdinalIgnoreCase)).TakeLast(2000));
    public int Seconds { get => _seconds; set => Set(ref _seconds, Math.Clamp(value, 1, 3600)); }
    public string Script { get => _script; set => Set(ref _script, value); }
    public string TestJson { get => _testJson; set => Set(ref _testJson, value); }
    public string RecordDirectory { get => _recordDirectory; private set => Set(ref _recordDirectory, value); }
    public string Clipboard { get => _clipboard; set => Set(ref _clipboard, value); }
    public bool PreviewEnabled { get => _previewEnabled; set => Set(ref _previewEnabled, value); }
    public ScreenObservation? Observation { get => _observation; private set => Set(ref _observation, value); }
    public PreviewFrame? Preview { get => _preview; private set => Set(ref _preview, value); }
    public int Width { get => _width; set => Set(ref _width, value); }
    public int Height { get => _height; set => Set(ref _height, value); }
    public int Density { get => _density; set => Set(ref _density, value); }
    public int RefreshRate { get => _refreshRate; set => Set(ref _refreshRate, value); }
    public int MemoryMb { get => _memoryMb; set => Set(ref _memoryMb, value); }
    private int _startAvailableMb;
    public int StartAvailableMb { get => _startAvailableMb; set => Set(ref _startAvailableMb, value); }
    private bool _lowRam;
    private int _vmHeapMb = 576;
    public bool LowRam { get => _lowRam; set => Set(ref _lowRam, value); }
    public int Cores { get => _cores; set => Set(ref _cores, value); }
    public string SelectedRenderer { get => _selectedRenderer; set => Set(ref _selectedRenderer, value); }
    public bool Vulkan { get => _vulkan; set => Set(ref _vulkan, value); }
    public bool DesktopDisplay { get => _desktopDisplay; set => Set(ref _desktopDisplay, value); }

    public AsyncAction RefreshCommand { get; }
    public AsyncAction RefreshSessionCommand { get; }
    public AsyncAction CancelSessionTaskCommand { get; }
    public AsyncAction StartCommand { get; }
    public AsyncAction StopCommand { get; }
    public AsyncAction OpenAndroidCommand { get; }
    public AsyncAction CaptureCommand { get; }
    public AsyncAction WakeCommand { get; }
    public AsyncAction ReleaseCommand { get; }
    public AsyncAction RefreshApplicationsCommand { get; }
    public AsyncAction InspectApkCommand { get; }
    public AsyncAction InstallCommand { get; }
    public AsyncAction LaunchCommand { get; }
    public AsyncAction StopApplicationCommand { get; }
    public AsyncAction ManageFilesCommand { get; }
    public AsyncAction BrowseFilesCommand { get; }
    public AsyncAction ParentFolderCommand { get; }
    public AsyncAction UploadCommand { get; }
    public AsyncAction CompareCommand { get; }
    public AsyncAction SyncCommand { get; }
    public AsyncAction LogsCommand { get; }
    public AsyncAction MetricsCommand { get; }
    public AsyncAction RecordCommand { get; }
    public AsyncAction FrameSampleCommand { get; }
    public AsyncAction TraceCommand { get; }
    public AsyncAction CancelCommand { get; }
    public AsyncAction RefreshCheckpointsCommand { get; }
    public AsyncAction CreateCheckpointCommand { get; }
    public AsyncAction RecoverCommand { get; }
    public AsyncAction ApplyProfileCommand { get; }
    public AsyncAction RefreshRuntimeCommand { get; }
    public AsyncAction ShellCommand { get; }
    public AsyncAction RootShellCommand { get; }
    public AsyncAction RunTestCommand { get; }
    public AsyncAction ClipboardReadCommand { get; }
    public AsyncAction ClipboardWriteCommand { get; }

    private AsyncAction Action(Func<Task> action, Func<bool>? canExecute = null)
    {
        var command = new AsyncAction(action, canExecute); command.Failed += error => Error = error.Message; _commands.Add(command); return command;
    }
    private void RefreshCommands() { foreach (var command in _commands) command.Refresh(); }
    private static string Text(JsonElement element, string property, string fallback = "") => element.TryGetProperty(property, out var value) ? value.GetString() ?? fallback : fallback;
    private static string Pretty(JsonElement value) { var text = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }); return text.Length > 32000 ? text[..32000] + "\n…界面已截断，完整结果请查看记录目录。" : text; }
    private string JoinRemote(string name) => (RemoteFolder.TrimEnd('/') + "/" + name).TrimStart('/');
    private DebugRequest FileRequest(string command, string remote, string local) => DebugRequest.Create(command, new { package = Package, scope = Scope.Value, remote, local });

    public async Task<JsonElement?> RunAsync(string title, DebugRequest request, bool exclusive = false, Action<JsonElement>? progressObserver = null)
    {
        if (exclusive && IsBusy) return null;
        var item = new WorkItem(title); Work.Insert(0, item); SelectedWork = item;
        foreach (var old in Work.Where(row => row.Completed).Reverse().Take(Math.Max(0, Work.Count - 100)).ToArray()) Work.Remove(old);
        if (exclusive) IsBusy = true;
        Error = "";
        try
        {
            var result = await _api.ExecuteAsync(request, _lifetime.Token, update =>
            {
                item.Id = Text(update, "jobId"); item.Status = Text(update, "status") switch { "running" => "执行中", "cancelling" => "正在取消", "queued" => "排队中", "cancelled" => "已取消", "timed_out" => "已超时", "interrupted" => "已中断", var status => status };
                if (update.TryGetProperty("stage", out var stage)) SessionHeadline = "会话摘要 · " + title + " · " + stage.GetString();
                if (update.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Object && progress.TryGetProperty("directory", out var directory) && directory.ValueKind == JsonValueKind.String)
                { item.Directory = directory.GetString(); RecordDirectory = item.Directory ?? ""; }
                RefreshCommands();
                progressObserver?.Invoke(update);
            });
            item.Details = Pretty(result); RawResult = item.Details; item.Status = "已完成"; Message = title + "已完成";
            var completedStage = result.ValueKind == JsonValueKind.Object ? Text(result, "stage") : "";
            if (completedStage is "process_observed" or "activity_ready") { item.Status = completedStage == "activity_ready" ? "Activity就绪" : "进程已出现"; Message = item.Status + "；页面可操作性仍待核验。"; }
            else if (request.Command == "release") { item.Status = "释放指令已确认"; Message = "释放指令已确认；应用内状态请结合观察核验。"; }
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("directory", out var directory) && directory.ValueKind == JsonValueKind.String) { item.Directory = directory.GetString(); RecordDirectory = item.Directory ?? ""; }
            return result;
        }
        catch (OperationCanceledException) { item.Details = "任务已取消。"; item.Status = "已取消"; Message = title + "已取消"; return null; }
        catch (DebugException error) when (error.Code is "cancelled" or "timeout" or "interrupted")
        { item.Details = error.Message; item.Status = error.Code switch { "cancelled" => "已取消", "timeout" => "已超时", _ => "已中断" }; Error = error.Message; return null; }
        catch (Exception error) { item.Details = error.Message; item.Status = "未完成"; Error = error.Message; return null; }
        finally { item.Completed = true; if (exclusive) IsBusy = false; RefreshCommands(); }
    }

    public Task RefreshAsync() => RefreshSessionAsync(false);
    public async Task RefreshSessionAsync(bool force)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var result = await _api.ExecuteAsync(DebugRequest.Create("session.summary", new { package = Package, refresh = force }), _lifetime.Token);
            var status = result.GetProperty("runtime");
            var refreshedSession = result.GetProperty("summary").Deserialize<SessionSummary>(DebugJson.Options)!;
            if (_session is not null && (_session.Instance != refreshedSession.Instance || _session.Session != refreshedSession.Session || DataRoot != Text(status, "dataRoot")))
            { SelectedApplication = null; Applications.Clear(); FilterApplications(); }
            _session = refreshedSession;
            SessionText = _session.Text;
            SessionArtifactDirectory = _session.ArtifactDirectory;
            SessionHeadline = "会话摘要 · " + _session.Status + " · PID " + (_session.App?.Pid ?? "未观察") + " · " + (_session.Tasks.FirstOrDefault()?.Stage ?? "无近期任务");
            RefreshCommands();
            IsRunning = Text(status, "status") == "Running";
            Status = Text(status, "status") switch { "Running" => "安卓运行中", "NotInstalled" => "需要安装运行环境", "Unreachable" => "安卓进程存在，连接不可用", "OperationInProgress" => "安卓操作进行中", _ => "安卓已停止" };
            DataRoot = Text(status, "dataRoot"); Serial = Text(status, "serial");
            FileWorkspace.UpdateContext(IsRunning, _session.Session, DataRoot);
            if (status.TryGetProperty("hostMemory", out var host))
                HostMemory = $"可用 {host.GetProperty("availableMb").GetInt64() / 1024d:0.0} / {host.GetProperty("totalMb").GetInt64() / 1024d:0.0} GiB";
            RootStatus = status.TryGetProperty("root", out var root) ? root.GetBoolean() ? "Root 可用" : "Root 不可用" : "等待启动";
            StatusDetail = IsRunning && status.TryGetProperty("state", out var state) ?
                state.GetProperty("locked").GetBoolean() ? "安卓处于锁屏状态" : state.GetProperty("awake").GetBoolean() ? Text(state, "foreground", "安卓已连接") : "安卓已息屏" : Text(status, "reason", "启动后可打开应用或进行调试");
            if (status.TryGetProperty("memoryProtection", out var protection) && protection.ValueKind == JsonValueKind.Object)
                StatusDetail = Text(protection, "message", StatusDetail);
        }
        catch (Exception error) { IsRunning = false; Status = "连接不可用"; StatusDetail = error.Message; }
        finally { _refreshing = false; }
    }
    public async Task RefreshRuntimeAsync()
    {
        var result = await RunAsync("读取运行配置", new("runtime.inspect")); if (result is not { } data) return;
        var profile = data.GetProperty("requested").Deserialize<RuntimeProfile>(DebugJson.Options)!;
        Width = profile.Width; Height = profile.Height; Density = profile.Density; RefreshRate = profile.RefreshRate;
        MemoryMb = profile.MemoryMb; StartAvailableMb = profile.StartAvailableMb; LowRam = profile.LowRam; _vmHeapMb = profile.VmHeapMb;
        Cores = profile.CpuCores; SelectedRenderer = profile.Renderer; Vulkan = profile.Vulkan; DesktopDisplay = profile.DesktopDisplay;
        var host = data.GetProperty("host"); HostMemory = $"可用 {host.GetProperty("availableMb").GetInt64() / 1024d:0.0} / {host.GetProperty("totalMb").GetInt64() / 1024d:0.0} GiB";
        if (data.TryGetProperty("observed", out var observed) && observed.ValueKind == JsonValueKind.Object)
        {
            var rate = observed.TryGetProperty("activeRefreshRate", out var refresh) && refresh.ValueKind == JsonValueKind.Number ? $"{refresh.GetDouble():0.#} Hz" : "刷新率不可用";
            Display = $"{profile.Width} × {profile.Height} · {rate}";
            Renderer = Text(observed, "renderer", "不可用").Replace("GLES: ", "");
        }
        else { Display = $"下次启动：{Width} × {Height} · 请求 {RefreshRate} Hz"; Renderer = SelectedRenderer == "host" ? "硬件渲染" : SelectedRenderer; }
    }
    public async Task RefreshApplicationsAsync()
    {
        var session = _session?.Session;
        var dataRoot = DataRoot;
        var includeSystem = IncludeSystemApplications;
        var rows = new List<ApplicationRow>();
        var cursor = "";
        do
        {
            var result = await RunAsync("刷新应用", DebugRequest.Create("apps.list", new { userId = 0, includeSystem, includeIcons = true, pageSize = 200, cursor, refresh = cursor.Length == 0 }));
            if (result is not { } data) return;
            foreach (var entry in data.GetProperty("entries").EnumerateArray())
            {
                var package = Text(entry, "package");
                var running = entry.TryGetProperty("runningStateError", out var error) && error.ValueKind == JsonValueKind.String ? "运行状态未知" :
                    entry.TryGetProperty("runningPids", out var pids) && pids.GetArrayLength() > 0 ? "进程已出现" : "未观察到进程";
                rows.Add(new(package, Text(entry, "name", package), Text(entry, "appRef"), entry.GetProperty("userId").GetInt32(),
                    Text(entry, "iconPath", null!), entry.GetProperty("system").GetBoolean(), Text(entry, "installationRevision"), running));
            }
            cursor = Text(data, "nextCursor");
        } while (cursor.Length > 0);
        if (session != _session?.Session || dataRoot != DataRoot) return;
        if (includeSystem != IncludeSystemApplications) return;
        var selection = SelectedApplication;
        foreach (var old in Applications.Where(app => rows.All(row => row.AppRef != app.AppRef)).ToArray()) Applications.Remove(old);
        foreach (var row in rows)
        {
            var old = Applications.FirstOrDefault(app => app.AppRef == row.AppRef);
            if (old is null) Applications.Add(row); else old.RefreshMetadata(row);
        }
        SelectedApplication = Applications.FirstOrDefault(app => app.AppRef == selection?.AppRef && app.AppRef is { Length: > 0 });
        FilterApplications();
    }
    private void FilterApplications()
    {
        var query = ApplicationFilter.Trim();
        var visible = Applications.Where(app => app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || app.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase).ThenBy(app => app.Package).ToArray();
        foreach (var old in FilteredApplications.Where(app => !visible.Contains(app)).ToArray()) FilteredApplications.Remove(old);
        for (var index = 0; index < visible.Length; index++)
        {
            var old = FilteredApplications.IndexOf(visible[index]);
            if (old < 0) FilteredApplications.Insert(index, visible[index]);
            else if (old != index) FilteredApplications.Move(old, index);
        }
    }
    public async Task InspectApkAsync()
    {
        var result = await RunAsync("检查 APK", DebugRequest.Create("apk.inspect", new { path = ApkPath })); if (result is not { } data) return;
        ApkSummary = $"{Text(data, "package")}\n版本 {Text(data, "versionName")} · {string.Join(", ", data.GetProperty("abis").EnumerateArray().Select(abi => abi.GetString()))}";
    }
    public async Task BrowseFilesAsync()
    {
        if (!CanBrowseFiles) { Message = "请先启动安卓并选择应用，或选择共享目录。"; return; }
        var target = (Package, Scope.Value, RemoteFolder, _session?.Session, DataRoot);
        Files.Clear(); SelectedFile = null;
        var result = await RunAsync("读取目录", FileRequest("files.list", RemoteFolder, "")); if (result is not { } data) return;
        if (target != (Package, Scope.Value, RemoteFolder, _session?.Session, DataRoot)) return;
        Files.Clear(); foreach (var row in data.GetProperty("entries").EnumerateArray().Select(FileRow.Parse).OrderByDescending(row => row.IsDirectory).ThenBy(row => row.Name)) Files.Add(row);
    }
    public async Task EnterSelectedFolderAsync() { if (SelectedFile is { IsDirectory: true } file) { RemoteFolder = JoinRemote(file.Name); await BrowseFilesAsync(); } }
    public async Task DownloadAsync(string destination)
    {
        if (SelectedFile is not { } file) { Error = "请先选择文件或文件夹。"; return; }
        await RunAsync(file.IsDirectory ? "导出文件夹" : "下载文件", FileRequest(file.IsDirectory ? "files.export" : "files.pull", JoinRemote(file.Name), destination));
    }
    public async Task RefreshCheckpointsAsync()
    {
        var result = await RunAsync("读取检查点", new("checkpoint.list")); if (result is not { } data) return;
        Checkpoints.Clear(); foreach (var entry in data.EnumerateArray()) Checkpoints.Add(new(Text(entry, "id"), Text(entry, "path")));
    }
    public async Task RestoreSelectedAsync()
    {
        if (SelectedCheckpoint is null) return;
        await RunAsync("恢复检查点", DebugRequest.Create("checkpoint.restore", new { id = SelectedCheckpoint.Id }), true); await RefreshAsync();
    }
    public async Task UninstallConfirmedAsync()
    {
        if (await RunAsync("卸载应用", DebugRequest.Create("uninstall", new { package = Package, confirm = true }), true) is not null) await RefreshApplicationsAsync();
    }
    public async Task ApplyProfileAsync()
    {
        var requested = new RuntimeProfile(SelectedRenderer, Width, Height, Density, RefreshRate, MemoryMb, Cores, Vulkan, Width >= Height ? "landscape" : "portrait", DesktopDisplay, StartAvailableMb, LowRam, _vmHeapMb); requested.Validate();
        var resume = IsRunning;
        if (resume && await RunAsync("保存并停止安卓", new("stop"), true) is null) return;
        if (await RunAsync("保存运行配置", DebugRequest.Create("runtime.configure", new { profile = requested }), true) is null) { await RefreshAsync(); return; }
        if (resume) await RunAsync("应用新配置并启动", new("start"), true);
        var failure = Error;
        await RefreshAsync(); await RefreshRuntimeAsync();
        if (failure.Length > 0) Error = failure;
    }
    public async Task CaptureAsync()
    {
        var result = await RunAsync("保存截图", new("screen")); if (result is { } data) { Observation = data.Deserialize<ScreenObservation>(DebugJson.Options); RecordDirectory = Path.GetDirectoryName(Observation!.Path)!; }
    }
    public async Task RefreshPreviewAsync()
    {
        if (!IsRunning || IsBusy || !PreviewEnabled || Section != WorkstationSection.Android) return;
        try { Preview = await _api.PreviewAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Message = "预览暂不可用：" + error.Message; }
    }
    public async Task KeyAsync(string key) => await RunAsync("安卓按键", DebugRequest.Create("key", new { key }));
    public async Task GestureAsync(PreviewMetadata metadata, double x0, double y0, double x1, double y1, int milliseconds)
    {
        var captured = await _api.ExecuteAsync(new("screen"), _lifetime.Token); var screen = captured.Deserialize<ScreenObservation>(DebugJson.Options)!;
        var nativeWidth = screen.ImageRotation is 90 or 270 ? screen.Height : screen.Width;
        var nativeHeight = screen.ImageRotation is 90 or 270 ? screen.Width : screen.Height;
        if (screen.Session != metadata.Session || screen.Foreground != metadata.Foreground || nativeWidth != metadata.NativeWidth || nativeHeight != metadata.NativeHeight || screen.Rotation != metadata.Rotation || screen.ImageRotation != metadata.ImageRotation)
        { Error = "画面环境已变化，请刷新预览后重试。"; return; }
        var frames = new List<InputFrame>(); var duration = Math.Clamp(milliseconds, 60, 10000);
        var steps = Math.Abs(x0 - x1) + Math.Abs(y0 - y1) > .02 ? Math.Clamp(duration / 25, 2, 400) : 1;
        for (var i = 0; i < steps; i++) { var t = i / (double)steps; frames.Add(new((int)(t * duration), [new(0, (int)(Math.Clamp(x0 + (x1 - x0) * t, 0, 1) * (screen.Width - 1)), (int)(Math.Clamp(y0 + (y1 - y0) * t, 0, 1) * (screen.Height - 1)))])); }
        frames.Add(new(duration, [new(0, (int)(Math.Clamp(x1, 0, 1) * (screen.Width - 1)), (int)(Math.Clamp(y1, 0, 1) * (screen.Height - 1)), 0)]));
        await RunAsync("调试手势", DebugRequest.Create("input", new { observation = screen.Id, frames }));
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _lifetime.Cancel(); _lifetime.Dispose(); } }
}
