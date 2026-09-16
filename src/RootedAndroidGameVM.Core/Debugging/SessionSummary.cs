using System.Text.Json;
using System.Text.RegularExpressions;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record PageEvidence(string Page, string Source, DateTimeOffset ObservedAt, string EvidencePath,
    string Session, string? AppPid, string ObservationId, long Revision, bool Superseded = false);
public sealed record SessionTaskSummary(string JobId, string RequestId, string Command, string Stage, string Status,
    string? Session, string? Pid, string ArtifactDirectory, string? ResultPath, string? ErrorCode);
public sealed record SessionImportSummary(string ImportId, string Stage, bool ContentVerified, int ExactFiles, int ExpectedFiles,
    int MetadataRewrites, string? ErrorCode, DateTimeOffset ObservedAt, string Session, string Directory, string EvidencePath);
public sealed record SessionTransferSummary(string Command, string Package, string Scope, string Remote, string Status,
    string? Sha256, DateTimeOffset RecordedAt, string EvidencePath);
public sealed record SessionSummary(DateTimeOffset ObservedAt, string Instance, string Status, string? Session,
    string Package, ApplicationReadiness? App, PageEvidence? Page, SessionTaskSummary[] Tasks,
    SessionImportSummary? Files, InputReleaseEvidence? Input, int[] ActiveOwnedSlots, string ArtifactDirectory,
    string[] RecoveryPoints, string NextAction, DebugRequest? ResumeRequest, string Text, SessionTransferSummary? Transfer = null);
public sealed record SessionObservation(JsonElement Runtime, ApplicationReadiness? App, PageEvidence? Page, string Status,
    string? Session, string Instance, string DataRoot, SessionImportSummary? Files, InputReleaseEvidence? Input,
    int[] ActiveOwnedSlots, bool PendingRestore);

public static class SessionSummaryText
{
    public static string Render(SessionSummary summary)
    {
        var task = summary.Tasks.FirstOrDefault();
        var file = summary.Files;
        var page = summary.Page;
        var app = summary.App;
        var release = summary.Input;
        var transfer = summary.Transfer;
        var filesText = transfer is not null && (file is null || transfer.RecordedAt > file.ObservedAt)
            ? $"最近文件核验：{transfer.Command} · {transfer.Scope}/{transfer.Remote} · {transfer.Status}（{transfer.RecordedAt:HH:mm:ss zzz}；App读取另验）"
            : file is null ? "最近文件核验：无记录" : $"最近文件核验：{(file.ContentVerified ? "内容已核验" : "未通过/未完成")} · 严格匹配 {file.ExactFiles}/{file.ExpectedFiles} · 已知元数据 {file.MetadataRewrites} · {file.ObservedAt:HH:mm:ss zzz}";
        return string.Join('\n',
            $"实例：{summary.Instance} · {summary.Status} · 会话 {summary.Session ?? "未观察到"}",
            $"App：{summary.Package} · PID {app?.Pid ?? "未观察到"} · {app?.Stage ?? "未观察"}" +
                (app is null ? "" : $"（{app.ObservedAt:HH:mm:ss zzz}观察，页面另验）"),
            page is null ? "页面：未验证；Activity/PID不能代替页面证据" :
                $"页面最近观察：{page.Page} · 截图标注 · {page.ObservedAt:HH:mm:ss zzz}" + (page.Superseded ? " · 已过期/操作后未确认" : " · 对应所引用截图") + "\n页面证据：" + page.EvidencePath,
            task is null ? "任务：无近期操作" : $"任务：{task.Command} · {task.Stage} · {task.Status} · {task.JobId}",
            filesText,
            $"触点：本工具记录 {summary.ActiveOwnedSlots.Length} 个；" + (release is null ? "释放状态未验证" : release.State switch
            {
                "acknowledged" => $"释放指令已确认（{release.ObservedAt:HH:mm:ss zzz}）；游戏内是否释放需实测",
                "instance_stopped" => "对应实例已停止",
                _ => "释放未确认：" + release.ErrorCode
            }),
            "产物：" + summary.ArtifactDirectory,
            "可恢复点：" + (summary.RecoveryPoints.Length == 0 ? "无已识别恢复点" : string.Join("；", summary.RecoveryPoints)),
            "下一步：" + summary.NextAction);
    }

    public static bool CanResume(SessionImportSummary? import) => import is { ContentVerified: false } &&
        import.ErrorCode is not ("import_content_mismatch" or "transfer_mismatch" or "transfer_not_verified" or "import_source_unavailable") &&
        import.Stage is "failed" or "cancelled" or "waiting_for_app" or "waiting_for_unpack" or "import_triggered" or "transferred";
}

public sealed partial class AndroidDebugService
{
    private ApplicationReadiness? _summaryApp;
    private string? _summarySession;
    private readonly SemaphoreSlim _summaryGate = new(1, 1);
    private PageEvidence? _pageEvidence;
    private long _pageRevision;

    public async Task<SessionObservation> SessionObservationAsync(string package, bool refresh, CancellationToken ct)
    {
        Android.AndroidPackageName.Parse(package);
        var runtime = JsonSerializer.SerializeToElement(await StatusAsync(ct), DebugJson.Options);
        var status = runtime.GetProperty("status").GetString()!;
        var session = runtime.TryGetProperty("session", out var value) ? value.GetString() : null;
        SessionObservation Snapshot(ApplicationReadiness? app, PageEvidence? page) => new(runtime, app, page, status, session,
            Options.Serial, Paths.ProductRoot, package == MalodyPackage ? LatestImportSummary() : null, LastInputRelease,
            Transport.ActiveTouchIds, Checkpoints.HasPendingRestore);
        if (status != "Running") return Snapshot(null, null);
        await _summaryGate.WaitAsync(ct);
        try
        {
            if (refresh || _summaryApp is null || _summaryApp.Package != package || _summarySession != session ||
                DateTimeOffset.UtcNow - _summaryApp.ObservedAt > TimeSpan.FromSeconds(10))
            {
                _summaryApp = await ObserveApplicationAsync(package, NewRecord("session-observation"), ct);
                _summarySession = session;
            }
            var page = _pageEvidence;
            if (page is not null && (page.Session != session || page.AppPid != _summaryApp.Pid || _summaryApp.Foreground != package || !_summaryApp.ActivityReady || page.Revision != Interlocked.Read(ref _pageRevision) ||
                DateTimeOffset.UtcNow - page.ObservedAt > TimeSpan.FromSeconds(30))) page = page with { Superseded = true };
            return Snapshot(_summaryApp, page);
        }
        finally { _summaryGate.Release(); }
    }

    public async Task<PageEvidence> ObservePageAsync(DebugRequest request, CancellationToken ct)
    {
        var page = request.Text("page");
        if (page is not ("home" or "song_select" or "loading" or "playing" or "paused" or "results" or "dialog" or "unknown"))
            throw new ArgumentException("page须为home/song_select/loading/playing/paused/results/dialog/unknown。");
        ScreenObservation observation;
        lock (_observations) observation = _observations.GetValueOrDefault(request.Text("observation"))
            ?? throw new DebugException("stale_observation", "请先读取新的Malody截图再记录页面观察。");
        InputSessionPolicy.RequireSame(observation.Session, Transport.Session);
        if (observation.Foreground != MalodyPackage || observation.Revision != Interlocked.Read(ref _pageRevision) ||
            DateTimeOffset.UtcNow - observation.CapturedAt > TimeSpan.FromSeconds(30))
            throw new DebugException("stale_observation", "页面观察必须引用30秒内、同会话且之后未执行操作的Malody截图。");
        var pid = (await ShellAsync("pidof " + Q(MalodyPackage) + " || true", false, ct)).Trim();
        if (pid.Length == 0 || pid != observation.AppPid) throw new DebugException("stale_observation", "截图后的Malody进程已改变。");
        var evidence = new PageEvidence(page, "operator_screenshot_observation", observation.CapturedAt, observation.Path,
            observation.Session, observation.AppPid, observation.Id, observation.Revision);
        await File.WriteAllTextAsync(Path.ChangeExtension(observation.Path, ".page.json"), DebugJson.Write(evidence), ct);
        _pageEvidence = evidence;
        return evidence;
    }

    public SessionImportSummary? LatestImportSummary()
    {
        var root = Path.Combine(Paths.ProductRoot, "debug-runs", "imports");
        if (!Directory.Exists(root)) return null;
        var path = new DirectoryInfo(root).EnumerateDirectories().Where(directory => Regex.IsMatch(directory.Name, "^[a-f0-9]{32}$"))
            .Select(directory => new FileInfo(Path.Combine(directory.FullName, "import.json"))).Where(file => file.Exists)
            .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        if (path is null || path.Length > 1024 * 1024) return null;
        Storage.StoragePathPolicy.RejectReparsePoints(path.FullName);
        using var document = JsonDocument.Parse(File.ReadAllText(path.FullName));
        var record = document.RootElement;
        var verified = record.TryGetProperty("imported", out var import) && import.GetProperty("verified").GetBoolean();
        return new(record.GetProperty("importId").GetString()!, record.GetProperty("stage").GetString()!, verified,
            import.ValueKind == JsonValueKind.Object ? import.GetProperty("verifiedFiles").GetInt32() : 0,
            import.ValueKind == JsonValueKind.Object ? import.GetProperty("expectedFiles").GetInt32() : 0,
            import.ValueKind == JsonValueKind.Object ? import.GetProperty("metadataRewrites").GetArrayLength() : 0,
            record.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null,
            record.GetProperty("updatedAt").GetDateTimeOffset(), record.GetProperty("session").GetString()!, path.DirectoryName!, path.FullName);
    }
}

public sealed partial class DebugBroker
{
    private SessionObservation? _lastSessionObservation;
    private async Task<object> SessionSummaryAsync(DebugRequest request, CancellationToken ct)
    {
        var package = request.Text("package", AndroidDebugService.MalodyPackage);
        SessionObservation? observed = null;
        lock (_leaseLock)
        {
            if (_exclusive)
            {
                // Start/stop/restore must still have a non-blocking progress view. Do not
                // read the guest or a directory being switched by an exclusive operation.
                var root = _service.Paths.ProductRoot;
                var cached = _lastSessionObservation?.DataRoot == root ? _lastSessionObservation : null;
                var runtime = JsonSerializer.SerializeToElement(new { status = "OperationInProgress", serial = _service.Options.Serial,
                    dataRoot = root, reason = "独占任务正在执行；当前安卓状态尚未重新核验。" }, DebugJson.Options);
                observed = new(runtime, null, cached?.Page is { } page ? page with { Superseded = true } : null,
                    "OperationInProgress", null, _service.Options.Serial, root,
                    package == AndroidDebugService.MalodyPackage ? cached?.Files : null, _service.LastInputRelease, [], false);
            }
        }
        if (observed is null)
        {
            var reply = await InvokeAsync(request with { Command = "session.observe" }, ct);
            if (!reply.Ok) throw DebugException.FromError(reply.Error!);
            observed = (SessionObservation)reply.Result!;
            _lastSessionObservation = observed;
        }
        var tasks = _jobs.Values.Where(job => job.Command is not ("app.observe" or "schema" or "licenses"))
            .OrderBy(job => job.Completed).ThenByDescending(job => job.Created).Take(6)
            .Select(job => new SessionTaskSummary(job.Id, job.Operation.RequestId, job.Command,
                job.Failure?.Stage ?? job.Stored?.Error?.Stage ?? job.Operation.Stage, job.Status,
                job.Operation.Session, job.Operation.Pid, job.Operation.DirectoryPath, job.Stored?.Path,
                job.Failure?.Error?.Code ?? job.Stored?.Error?.Code)).ToArray();
        var files = observed.Files;
        var recovery = new List<string>();
        if (observed.PendingRestore) recovery.Add("checkpoint.recover：中断的检查点恢复");
        if (files is not null) recovery.Add("导入 " + files.ImportId + "（" + files.Stage + "）");
        foreach (var task in tasks.Where(task => task.Status == "interrupted")) recovery.Add("中断任务 " + task.JobId + "，查看原产物后决定续作");
        var resume = SessionSummaryText.CanResume(files) ? DebugRequest.Create("malody.import", new { importId = files!.ImportId }) : null;
        var release = observed.Input;
        var next = observed.Status == "OperationInProgress" ? "等待当前独占任务完成，或取消相应任务；安卓状态暂未核验" :
            observed.Status == "Unreachable" ? "保留当前实例，检查连接和任务诊断；不要当作已停机重复启动" :
            observed.Status != "Running" ? "确认实例状态后启动安卓" :
            release is { State: "unverified" } ? "先释放触点并重新观察；不要假定输入已清理" :
            resume is not null ? "使用原importId继续导入；不重复上传" :
            files is { ContentVerified: false } ? "打开文件核验产物，处理缺失或差异" :
            observed.App?.Pid is null ? "启动目标App并观察Activity" :
            observed.Page is null || observed.Page.Superseded || observed.Page.Page == "unknown" ? "需要页面判断时获取新截图，并记录页面观察；Activity本身不足以判断页面" :
            files?.Stage == "waiting_for_activation" ? "在App内确认启用，再观察实际运行" : "按当前页面继续测试，结果与产物分别核验";
        var summary = new SessionSummary(DateTimeOffset.UtcNow, observed.Instance, observed.Status, observed.Session,
            package, observed.App, observed.Page, tasks, files, release, observed.ActiveOwnedSlots,
            tasks.FirstOrDefault()?.ArtifactDirectory ?? files?.Directory ?? Path.Combine(observed.DataRoot, "debug-runs"),
            recovery.ToArray(), next, resume, "", LatestTransferSummary(package));
        summary = summary with { Text = SessionSummaryText.Render(summary) };
        return new { runtime = observed.Runtime, summary };
    }

    private SessionTransferSummary? LatestTransferSummary(string package)
    {
        foreach (var job in _jobs.Values.Where(job => job.Completed && job.Command is "files.push" or "files.sync" or "files.diff")
            .OrderByDescending(job => job.Created).Take(16))
        {
            var requestPath = Path.Combine(job.Operation.DirectoryPath, "request.json");
            if (!File.Exists(requestPath) || new FileInfo(requestPath).Length > 65536) continue;
            Storage.StoragePathPolicy.RejectReparsePoints(requestPath);
            var request = JsonSerializer.Deserialize<DebugRequest>(File.ReadAllText(requestPath), DebugJson.Options)!;
            if (request.Text("package", AndroidDebugService.MalodyPackage) != package) continue;
            var result = job.Stored is { Bytes: <= 65536 } ? job.Stored.Read() : job.Failure;
            var data = result?.Result is JsonElement element ? element : default;
            var hash = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sha256", out var sha) ? sha.GetString() : null;
            var status = job.Status != "succeeded" ? "未完成：" + (job.Failure?.Error?.Code ?? job.Stored?.Error?.Code ?? job.Status) :
                hash is not null ? "SHA-256传输核验通过" : "完整比较/同步结果见产物";
            return new(job.Command, package, request.Text("scope", "external"), request.Text("remote"), status, hash,
                job.Stored is null ? job.Created : new DateTimeOffset(File.GetLastWriteTimeUtc(job.Stored.Path)),
                job.Stored?.Path ?? job.Operation.DirectoryPath);
        }
        return null;
    }
}
