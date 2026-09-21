using System.Collections.ObjectModel;
using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Ui.Workstation;

public delegate Task<JsonElement?> WorkstationOperation(string title, DebugRequest request, bool exclusive = false, Action<JsonElement>? progress = null);
public sealed class FileWorkspaceViewModel(WorkstationOperation run) : ObservableState
{
    private bool _running, _busy, _includeSystem;
    private string? _session; private string _dataRoot = "", _search = "", _address = "", _location = "请选择应用或共享存储", _message = "", _cursor = "";
    private int _generation, _scopeGeneration, _catalogGeneration, _total;
    private bool _loadingPage;
    private AndroidUser? _user; private ApplicationRow? _application;
    private FileRootDescriptor? _root; private string _relative = ""; private string? _directoryRef;
    private readonly Stack<(FileRootDescriptor Root, string Relative)> _back = new();
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private FileTransferHistoryRow? _selectedTransfer;
    private string? _currentJobId; private string _progress = "";
    private string? _activePlanId, _activeHistoryStatus, _activeHistoryDataRoot;
    private DateTimeOffset _activeHistoryObservedAt;
    public ApplicationRow? RequestedApplication { get; set; }
    public ObservableCollection<AndroidUser> Users { get; } = [];
    public ObservableCollection<ApplicationRow> Applications { get; } = [];
    public ObservableCollection<ApplicationRow> FilteredApplications { get; } = [];
    public ObservableCollection<FileTreeNode> Tree { get; } = [];
    public ObservableCollection<FileEntryRow> Entries { get; } = [];
    public ObservableCollection<FileEntryRow> Selection { get; } = [];
    public ObservableCollection<FileBreadcrumb> Breadcrumbs { get; } = [];
    public ObservableCollection<FileTransferHistoryRow> Transfers { get; } = [];
    public FileRootDescriptor[] Roots { get; private set; } = [];
    public bool IsRunning => _running;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); CapabilitiesChanged(); } }
    public bool CanChangeScope => _running && !IsBusy;
    public bool CanUpload => CanChangeScope && _root is { Locked: false } &&
        (_root is { Accessible: true, Writable: true } && _directoryRef is not null || _root is { Exists: false, Creatable: true });
    public bool CanDownload => CanChangeScope && Selection.Count > 0;
    public bool CanExportDirectory => CanChangeScope && _directoryRef is not null;
    public bool CanExportApplication => CanChangeScope && SelectedApplication is not null && Roots.Any(root => root.Kind != "shared" && root.Accessible);
    public bool CanGoBack => CanChangeScope && _back.Count > 0;
    public bool CanGoUp => CanChangeScope && _root is not null && _relative.Length > 0;
    public bool HasMore => _cursor.Length > 0 && !IsBusy && !_loadingPage;
    public bool CanResumeSelected => CanChangeScope && SelectedTransfer?.CanResume == true;
    public bool CanInspectSelected => SelectedTransfer is not null;
    public bool CanCancel => IsBusy && _currentJobId is not null;
    public string ProgressText { get => _progress; private set => Set(ref _progress, value); }
    public string CountText => $"已显示 {Entries.Count} / {_total} 项 · 已选 {Selection.Count} 项";
    public string Location { get => _location; private set => Set(ref _location, value); }
    public string Address { get => _address; set => Set(ref _address, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) Filter(); } }
    public bool IncludeSystem { get => _includeSystem; set => Set(ref _includeSystem, value); }
    public AndroidUser? SelectedUser { get => _user; set { if (Set(ref _user, value)) { SelectedApplication = null; Applications.Clear(); Filter(); ClearLocation(); } } }
    public ApplicationRow? SelectedApplication { get => _application; set { if (Set(ref _application, value)) ClearLocation(); } }
    public FileTransferHistoryRow? SelectedTransfer { get => _selectedTransfer; set { Set(ref _selectedTransfer, value); Changed(nameof(CanResumeSelected)); Changed(nameof(CanInspectSelected)); } }
    public void UpdateContext(bool running, string? session, string dataRoot)
    {
        if (_session != session || _dataRoot != dataRoot || _running && !running)
        { ClearLocation(); Applications.Clear(); Filter(); SelectedApplication = null; }
        _running = running; _session = session; _dataRoot = dataRoot;
        Changed(nameof(IsRunning)); CapabilitiesChanged();
    }
    private void ClearLocation()
    {
        _generation++; _scopeGeneration++; Roots = []; Tree.Clear(); Entries.Clear(); Selection.Clear(); Breadcrumbs.Clear(); _back.Clear();
        _root = null; _directoryRef = null; _relative = ""; _cursor = ""; _total = 0;
        Address = ""; Location = "请选择应用或共享存储"; CapabilitiesChanged();
    }
    private void CapabilitiesChanged()
    {
        foreach (var name in new[] { nameof(CanChangeScope), nameof(CanUpload), nameof(CanDownload), nameof(CanExportDirectory), nameof(CanExportApplication), nameof(CanGoBack), nameof(CanGoUp), nameof(HasMore), nameof(CountText), nameof(CanResumeSelected), nameof(CanCancel) }) Changed(name);
    }
    public void SetSelection(IEnumerable<FileEntryRow> rows)
    { Selection.Clear(); foreach (var row in rows.Where(Entries.Contains)) Selection.Add(row); CapabilitiesChanged(); }
    private void Filter()
    {
        var desired = Applications.Where(app => app.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || app.Package.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (SelectedApplication is not null && !desired.Contains(SelectedApplication)) SelectedApplication = null;
        foreach (var old in FilteredApplications.Where(app => !desired.Contains(app)).ToArray()) FilteredApplications.Remove(old);
        foreach (var app in desired.Where(app => !FilteredApplications.Contains(app))) FilteredApplications.Add(app);
    }
    public async Task InitializeAsync()
    {
        if (IsBusy) return;
        await _initialization.WaitAsync();
        IsBusy = true;
        try
        {
            await RefreshTransfersAsync();
            if (!_running) { Message = "启动安卓后可浏览文件；历史任务仍可查看。"; return; }
            var session = _session;
            var result = await run("读取安卓用户", new("users.list"), false);
            if (result is not { } data || session != _session || !_running) return;
            var wanted = RequestedApplication?.UserId ?? SelectedUser?.UserId ?? 0;
            Users.Clear(); foreach (var user in data.GetProperty("users").Deserialize<AndroidUser[]>(DebugJson.Options)!) Users.Add(user);
            SelectedUser = Users.FirstOrDefault(user => user.UserId == wanted) ?? Users.FirstOrDefault();
            await RefreshApplicationsAsync();
            if (RequestedApplication is { } requested)
            { SelectedApplication = Applications.FirstOrDefault(app => app.AppRef == requested.AppRef); RequestedApplication = null; }
            await RefreshRootsAsync();
        }
        finally { IsBusy = false; _initialization.Release(); }
    }
    public async Task RefreshApplicationsAsync()
    {
        if (!_running || SelectedUser is null) return;
        var user = SelectedUser.UserId; var session = _session; var previous = SelectedApplication?.AppRef; var rows = new List<ApplicationRow>(); var cursor = "";
        var generation = ++_catalogGeneration; var includeSystem = IncludeSystem;
        do
        {
            var result = await run("读取应用目录", DebugRequest.Create("apps.list", new { userId = user, includeSystem, includeIcons = true, pageSize = 200, cursor, refresh = cursor.Length == 0 }), false);
            if (result is not { } data || generation != _catalogGeneration || !_running) return;
            foreach (var app in data.GetProperty("entries").Deserialize<CatalogApplication[]>(DebugJson.Options)!)
                rows.Add(new(app.Package, app.Name, app.AppRef, app.UserId, app.IconPath, app.System, app.InstallationRevision, app.RunningPids?.Length > 0 ? "进程已出现" : "未观察到进程"));
            cursor = data.TryGetProperty("nextCursor", out var next) ? next.GetString() ?? "" : "";
        } while (cursor.Length > 0);
        if (SelectedUser?.UserId != user || _session != session) return;
        Applications.Clear(); foreach (var app in rows) Applications.Add(app);
        SelectedApplication = Applications.FirstOrDefault(app => app.AppRef == previous && previous is not null); Filter();
    }
    public async Task RefreshRootsAsync()
    {
        if (!_running || SelectedUser is null) return;
        ClearLocation(); var generation = _generation; var app = SelectedApplication;
        var result = await run("读取数据目录", DebugRequest.Create("files.roots", new { appRef = app?.AppRef, userId = SelectedUser.UserId }), false);
        if (result is not { } data || generation != _generation) return;
        ApplyRoots(data.GetProperty("roots").Deserialize<FileRootDescriptor[]>(DebugJson.Options)!);
        var initial = Roots.FirstOrDefault(root => root.Accessible && root.Kind == (app is null ? "shared" : "private")) ?? Roots.FirstOrDefault(root => root.Accessible);
        if (initial is not null) await NavigateAsync(initial, "", false);
        else Message = "没有可访问的数据目录，请查看根目录状态。";
        CapabilitiesChanged();
    }
    private void ApplyRoots(FileRootDescriptor[] roots)
    {
        Roots = roots; Tree.Clear();
        foreach (var root in Roots)
        {
            var node = new FileTreeNode(root, "", root.Title);
            if (root.Accessible) node.Children.Add(new(root, "", "正在读取…"));
            Tree.Add(node);
        }
    }
    public async Task ExpandAsync(FileTreeNode node, string? cursor = null)
    {
        if (node.Loading || node.Loaded && cursor is null || !node.Accessible || node.IsMore) return;
        var generation = _scopeGeneration;
        node.Loading = true;
        try
        {
            var result = await run("读取目录树", DebugRequest.Create("files.browse", new { rootRef = node.Root.RootRef, relativePath = node.RelativePath, pageSize = 500, cursor }), false);
            if (result is not { } data || generation != _scopeGeneration) return;
            var page = data.Deserialize<FileBrowsePage>(DebugJson.Options)!;
            if (cursor is null) node.Children.Clear();
            else foreach (var more in node.Children.Where(child => child.IsMore).ToArray()) node.Children.Remove(more);
            foreach (var entry in page.Entries.Where(entry => entry.Kind == "directory"))
            { var child = new FileTreeNode(node.Root, entry.RelativePath, entry.Name); child.Children.Add(new(node.Root, "", "正在读取…")); node.Children.Add(child); }
            if (page.NextCursor is not null) node.Children.Add(new(node.Root, node.RelativePath, "加载更多目录…", page.NextCursor, node));
            node.Loaded = true;
        }
        finally { node.Loading = false; }
    }
    public Task OpenNodeAsync(FileTreeNode node) => node is { IsMore: true, Parent: { } parent } ? ExpandAsync(parent, node.Cursor) : NavigateAsync(node.Root, node.RelativePath);
    public Task OpenEntryAsync(FileEntryRow entry) => entry.IsDirectory && _root is not null ? NavigateAsync(_root, entry.Entry.RelativePath) : Task.CompletedTask;
    public async Task NavigateAsync(FileRootDescriptor root, string relative, bool remember = true)
    {
        FileReferences.Relative(relative);
        if (remember && _root is not null) _back.Push((_root, _relative));
        _generation++; var generation = _generation; _root = root; _relative = relative; _directoryRef = null; _cursor = ""; _total = 0; Entries.Clear(); Selection.Clear();
        Location = (SelectedApplication?.Name ?? "共享存储") + " · " + root.Title;
        Address = root.DisplayPath.TrimEnd('/') + (relative.Length == 0 ? "" : "/" + relative);
        Breadcrumbs.Clear(); Breadcrumbs.Add(new(root.Title, "")); var path = "";
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries)) { path = FileTransferPolicy.JoinRemote(path, part); Breadcrumbs.Add(new(part, path)); }
        CapabilitiesChanged();
        if (!root.Accessible)
        {
            Message = root is { Exists: false, Creatable: true, Locked: false }
                ? "此目录尚未生成。可选择上传文件或文件夹，确认计划后创建目录。"
                : root.Title + "：" + (root.Locked ? "用户未解锁" : root.Reason ?? "不可访问");
            return;
        }
        var result = await run("浏览文件", DebugRequest.Create("files.browse", new { rootRef = root.RootRef, relativePath = relative, pageSize = 200 }), false);
        if (result is not { } data || generation != _generation) return;
        ApplyPage(data.Deserialize<FileBrowsePage>(DebugJson.Options)!, false);
        Message = ""; CapabilitiesChanged();
    }
    private void ApplyPage(FileBrowsePage page, bool append)
    {
        if (!append) Entries.Clear();
        foreach (var entry in page.Entries) Entries.Add(new(entry));
        _directoryRef = page.Directory.EntryRef; _cursor = page.NextCursor ?? ""; _total = page.Total; CapabilitiesChanged();
    }
    public async Task LoadMoreAsync()
    {
        if (_root is null || !HasMore) return; var generation = _generation;
        _loadingPage = true; CapabilitiesChanged();
        try
        {
            var result = await run("读取下一页", DebugRequest.Create("files.browse", new { rootRef = _root.RootRef, relativePath = _relative, pageSize = 200, cursor = _cursor }), false);
            if (result is { } data && generation == _generation) ApplyPage(data.Deserialize<FileBrowsePage>(DebugJson.Options)!, true);
        }
        finally { _loadingPage = false; CapabilitiesChanged(); }
    }
    public Task UpAsync() => _root is null ? Task.CompletedTask : NavigateAsync(_root, _relative.Contains('/') ? _relative[.._relative.LastIndexOf('/')] : "");
    public Task BackAsync() { if (!_back.TryPop(out var previous)) return Task.CompletedTask; return NavigateAsync(previous.Root, previous.Relative, false); }
    public async Task RefreshDirectoryAsync()
    {
        if (_root is not { } root) return;
        var relative = _relative; var generation = _scopeGeneration;
        if (!root.Accessible && root.Creatable)
        {
            var result = await run("刷新数据目录", DebugRequest.Create("files.roots", new { appRef = SelectedApplication?.AppRef, userId = SelectedUser?.UserId ?? 0 }), false);
            if (result is not { } data || generation != _scopeGeneration || _root != root) return;
            ApplyRoots(data.GetProperty("roots").Deserialize<FileRootDescriptor[]>(DebugJson.Options)!);
            if (Roots.FirstOrDefault(current => current.RootRef == root.RootRef) is not { } currentRoot) { ClearLocation(); Message = "目录身份已改变，请重新选择。"; return; }
            root = currentRoot;
        }
        await NavigateAsync(root, relative, false);
    }
    public Task OpenAddressAsync()
    {
        if (_root is null) return Task.CompletedTask;
        var address = Address.Trim().TrimEnd('/');
        var relative = address == _root.DisplayPath ? "" : address.StartsWith(_root.DisplayPath.TrimEnd('/') + "/", StringComparison.Ordinal) ? address[(_root.DisplayPath.TrimEnd('/').Length + 1)..] : address;
        return NavigateAsync(_root, relative);
    }
    public Task OpenBreadcrumbAsync(FileBreadcrumb crumb) => _root is null ? Task.CompletedTask : NavigateAsync(_root, crumb.RelativePath);
    public DebugRequest UploadRequest(IEnumerable<string> paths)
    {
        if (!CanUpload || _root is null) throw new InvalidOperationException("请先选择可上传的目录。");
        var createParents = _directoryRef is null;
        var destination = createParents ? new TransferDestination(RootRef: _root.RootRef, RelativePath: _relative) : new TransferDestination(EntryRef: _directoryRef);
        return DebugRequest.Create("files.transfer.plan", new
        { direction = "upload", sources = paths.Select(path => new { localPath = Path.GetFullPath(path) }).ToArray(), destination, createParents });
    }
    public DebugRequest DownloadRequest(string destination, bool currentDirectory = false, IEnumerable<FileRootDescriptor>? roots = null, string format = "directory")
    {
        var sources = roots is not null ? roots.Select(root => new TransferSource(RootRef: root.RootRef)).ToArray() : currentDirectory ? [new TransferSource(EntryRef: _directoryRef)] :
            Selection.Select(row => new TransferSource(EntryRef: row.Entry.EntryRef)).ToArray();
        return DebugRequest.Create("files.transfer.plan", new { direction = "download", format, sources, destination = new { localDirectory = Path.GetFullPath(destination) } });
    }
    public async Task<JsonElement?> PrepareAsync(DebugRequest request)
    { IsBusy = true; _currentJobId = null; Message = ""; ProgressText = "正在核对来源与目标…"; var generation = _scopeGeneration; try { var plan = await run("准备传输", request, false, CaptureProgress); return generation == _scopeGeneration ? plan : null; } finally { IsBusy = false; } }
    public async Task ExecuteAsync(string planId, string policy, bool stopApplications, string idempotencyKey)
    {
        IsBusy = true; _currentJobId = null; Message = ""; ProgressText = "正在开始传输…";
        try
        {
            await BeginTransferHistoryAsync(planId);
            var result = await run("传输文件", DebugRequest.Create("files.transfer.start", new { planId, conflictPolicy = policy, stopApplications, idempotencyKey }), true, CaptureProgress);
            Message = result is { } data && data.GetProperty("transferVerified").GetBoolean() ? "传输已核验；应用读取可另行确认。" : "传输未完成，请查看结果或继续原任务。";
        }
        finally { ClearActiveHistory(); IsBusy = false; await RefreshTransfersAsync(); SelectedTransfer = Transfers.FirstOrDefault(item => item.PlanId == planId); if (_running) { var message = Message; await RefreshDirectoryAsync(); Message = message; } }
    }
    public async Task ResumeAsync(FileTransferHistoryRow transfer)
    {
        IsBusy = true; _currentJobId = null; Message = ""; ProgressText = "正在核对原任务与暂存…";
        try
        {
            await BeginTransferHistoryAsync(transfer.PlanId);
            var result = await run("继续传输", DebugRequest.Create("files.transfer.resume", new { planId = transfer.PlanId, idempotencyKey = "gui-resume-" + Guid.NewGuid().ToString("N") }), true, CaptureProgress);
            Message = result is { } data && data.GetProperty("transferVerified").GetBoolean() ? "原任务已完成并核验。" : "原任务未完成，请查看结果。";
        }
        finally { ClearActiveHistory(); IsBusy = false; await RefreshTransfersAsync(); SelectedTransfer = Transfers.FirstOrDefault(item => item.PlanId == transfer.PlanId); if (_running) { var message = Message; await RefreshDirectoryAsync(); Message = message; } }
    }
    private async Task BeginTransferHistoryAsync(string planId)
    {
        _activePlanId = planId; _activeHistoryStatus = "starting"; _activeHistoryDataRoot = _dataRoot; _activeHistoryObservedAt = DateTimeOffset.UtcNow;
        await RefreshTransfersAsync(); SelectedTransfer = Transfers.FirstOrDefault(item => item.PlanId == planId);
    }
    private void ClearActiveHistory() { _activePlanId = null; _activeHistoryStatus = null; _activeHistoryDataRoot = null; }
    private void ApplyActiveHistory()
    {
        if (_activePlanId is null || _activeHistoryStatus is null || _activeHistoryDataRoot != _dataRoot ||
            Transfers.FirstOrDefault(item => item.PlanId == _activePlanId) is not { } row) return;
        var updated = row with { Status = _activeHistoryStatus, JobId = _currentJobId ?? row.JobId, CanResume = false, UpdatedAt = _activeHistoryObservedAt };
        var selected = SelectedTransfer?.PlanId == updated.PlanId;
        Transfers[Transfers.IndexOf(row)] = updated;
        if (selected) SelectedTransfer = updated;
    }
    public async Task RefreshTransfersAsync()
    {
        var dataRoot = _dataRoot;
        var result = await run("读取传输记录", new("files.transfer.list"), false);
        if (result is not { } data || dataRoot != _dataRoot) return;
        var selected = SelectedTransfer?.PlanId; Transfers.Clear();
        foreach (var entry in data.GetProperty("transfers").Deserialize<FileTransferHistoryRow[]>(DebugJson.Options)!) Transfers.Add(entry);
        ApplyActiveHistory();
        SelectedTransfer = Transfers.FirstOrDefault(entry => entry.PlanId == selected);
    }
    public Task<JsonElement?> InspectSelectedAsync() => SelectedTransfer is null ? Task.FromResult<JsonElement?>(null) : run("查看传输结果", DebugRequest.Create("files.transfer.inspect", new { planId = SelectedTransfer.PlanId }), false);
    public Task<JsonElement?> InspectAsync(string planId, int offset) => run("查看传输结果", DebugRequest.Create("files.transfer.inspect", new { planId, offset, pageSize = 100 }), false);
    public async Task CancelAsync() { if (_currentJobId is not null) await run("取消传输", DebugRequest.Create("cancel", new { id = _currentJobId }), false); }
    private void CaptureProgress(JsonElement update)
    {
        var previousJob = _currentJobId;
        if (update.TryGetProperty("jobId", out var id)) _currentJobId = id.GetString();
        if (_activePlanId is not null && update.TryGetProperty("status", out var status) &&
            (_activeHistoryStatus != status.GetString() || previousJob != _currentJobId))
        {
            _activeHistoryStatus = status.GetString(); _activeHistoryObservedAt = DateTimeOffset.UtcNow; ApplyActiveHistory();
        }
        if (update.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Object && progress.TryGetProperty("bytes", out var bytes) && progress.TryGetProperty("totalBytes", out var total))
            ProgressText = $"已传 {FileSizeText.Format(bytes.GetInt64())} / {FileSizeText.Format(total.GetInt64())}";
        else if (update.TryGetProperty("stage", out var stage)) ProgressText = "当前阶段：" + stage.GetString();
        Changed(nameof(CanCancel));
    }
}
