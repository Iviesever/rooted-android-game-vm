using System.Diagnostics;
using RootedAndroidGameVM.Core.Ui;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Buffers.Binary;
using System.Runtime.Versioning;
using Grpc.Core;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Debugging;

[SupportedOSPlatform("windows")]
public sealed partial class AndroidDebugService : IDisposable
{
    public static readonly AsyncLocal<Action<object>?> Progress = new();
    public readonly InstallPaths Paths;
    public readonly AndroidSdkLayout Layout;
    public readonly AndroidVmOptions Options;
    public readonly OwnedInstance Instance;
    public readonly EmulatorDebugTransport Transport;
    public readonly ColdCheckpoint Checkpoints;
    private AndroidVmController _controller;
    private readonly Dictionary<string, ScreenObservation> _observations = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private (bool Boot, bool Awake, bool Locked, string? Foreground, int Rotation)? _cachedState;
    private string? _cachedStateSession;
    private long _stateAt;
    private InputReleaseEvidence? _lastRelease;
    public InputReleaseEvidence? LastInputRelease => _lastRelease;
    public Func<MemoryProtectionNotice?>? MemoryNotice { get; set; }
    public AndroidDebugService(InstallPaths? paths = null)
    {
        Paths = paths ?? InstallPaths.CreateDefault(); Layout = AndroidSdkLayout.Discover(Paths);
        Options = AndroidVmOptions.ForPaths(Paths) with { GrpcPort = 8554 };
        Instance = new(Paths, Options); Transport = new(Instance, Options.GrpcPort.Value);
        Checkpoints = new(Paths, Instance); _controller = new(Layout, Options);
    }
    public string NewRecord(string kind)
    {
        var path = Path.Combine(Paths.ProductRoot, "debug-runs", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + kind + "-" + Guid.NewGuid().ToString("N")[..8]);
        ColdCheckpoint.Restrict(path); Progress.Value?.Invoke(new { directory = path, kind }); return path;
    }
    public async Task<string> AdbAsync(string[] args, CancellationToken ct, int limit = 8 * 1024 * 1024)
    {
        var host = Instance.Require();
        if (DebugOperation.Current.Value is { } operation) operation.Session = $"{host.ProcessId}:{host.StartedAtUtcTicks}";
        return await BinaryProcess.RunTextAsync(AndroidCommandFactory.Adb(Layout, Options, args), limit, ct);
    }
    public async Task<string> ShellAsync(string script, bool root, CancellationToken ct, bool strict = true)
    {
        var host = Instance.Require(force: strict);
        if (DebugOperation.Current.Value is { } operation) operation.Session = $"{host.ProcessId}:{host.StartedAtUtcTicks}";
        var spec = root ? AndroidCommandFactory.RootShell(Layout, Options, script) : AndroidCommandFactory.Adb(Layout, Options, "shell", script);
        return await BinaryProcess.RunTextAsync(spec, 8 * 1024 * 1024, ct);
    }
    public static string Q(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public async Task<object> StatusAsync(CancellationToken ct)
    {
        var status = await _controller.GetStatusAsync(ct);
        if (status != VmStatus.Running)
        {
            if (status != VmStatus.NotInstalled)
            {
                try { Instance.RequireStopped(); }
                catch (DebugException error) when (error.Code == "instance_busy")
                {
                    return new
                    {
                        version = "0.5.0",
                        status = "Unreachable",
                        dataRoot = Paths.ProductRoot,
                        serial = Options.Serial,
                        instanceActive = true,
                        reason = "产品进程仍在运行，但ADB未确认连接；不能当成已停机。",
                        hostMemory = HostMemory.Read(),
                        memoryProtection = MemoryNotice?.Invoke()
                    };
                }
            }
            return new { version = "0.5.0", status = status.ToString(), dataRoot = Paths.ProductRoot, serial = Options.Serial, hostMemory = HostMemory.Read(), memoryProtection = MemoryNotice?.Invoke() };
        }
        var host = Instance.Require();
        var state = await StateAsync(ct);
        return new
        {
            version = "0.5.0",
            status = "Running",
            dataRoot = Paths.ProductRoot,
            serial = Options.Serial,
            hostMemory = HostMemory.Read(),
            memoryProtection = MemoryNotice?.Invoke(),
            session = $"{host.ProcessId}:{host.StartedAtUtcTicks}",
            state = new { bootCompleted = state.Boot, awake = state.Awake, locked = state.Locked, foreground = state.Foreground, rotation = state.Rotation },
            root = (await ShellAsync("id", true, ct)).Contains("uid=0"),
            abi = (await ShellAsync("getprop ro.product.cpu.abilist; getprop ro.dalvik.vm.native.bridge", false, ct)).Trim()
        };
    }
    public async Task StartAsync(CancellationToken ct, bool verifyingRestore = false)
    {
        if (Checkpoints.HasPendingRestore && !verifyingRestore) throw new DebugException("recovery_required", "上次检查点恢复被中断；请先执行 checkpoint.recover，禁止重新初始化虚拟机。");
        if (await _controller.GetStatusAsync(ct) == VmStatus.Running) { Instance.Require(); return; }
        Instance.RequirePortsFree(Options.GrpcPort!.Value);
        _controller = new(Layout, AndroidVmOptions.ForPaths(Paths) with { GrpcPort = Options.GrpcPort });
        await _controller.StartAsync(ct);
        try
        {
            Instance.Require(); ct.ThrowIfCancellationRequested();
            var startupRelease = await ReleaseAsync();
            if (!startupRelease.Acknowledged) throw new DebugException("input_release_unverified", "启动后未确认输入通道清理。", "releasing_input", startupRelease.EvidencePath);
            ct.ThrowIfCancellationRequested();
            await ApplyDesktopAppearanceAsync(new RuntimeProfileStore(Paths).Read().DesktopDisplay, ct, settleStartup: true);
        }
        catch (Exception error)
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await StopAsync(stopDeadline.Token); }
            catch (Exception cleanup)
            { throw new DebugException("start_cleanup_unverified", error.Message + " 启动后清理未确认：" + cleanup.Message, "startup_cleanup", inner: error); }
            throw;
        }
    }
    public async Task StopAsync(CancellationToken ct)
    {
        if (await _controller.GetStatusAsync(ct) != VmStatus.Running) { Instance.RequireStopped(); return; }
        Instance.Require(); var release = await ReleaseAsync();
        await _controller.StopAsync(ct); await Instance.WaitStoppedAsync(ct);
        Transport.Dispose();
        var stoppedEvidence = Path.ChangeExtension(release.EvidencePath, ".stopped.json");
        _lastRelease = release with
        {
            State = "instance_stopped",
            RemainingOwnedSlots = [],
            ErrorCode = null,
            ObservedAt = DateTimeOffset.UtcNow,
            EvidencePath = stoppedEvidence
        };
        await File.WriteAllTextAsync(stoppedEvidence, DebugJson.Write(_lastRelease), CancellationToken.None);
        lock (_observations) _observations.Clear();
    }
    public async Task<InputReleaseEvidence> ReleaseAsync(string? expectedSession = null)
    {
        var directory = DebugOperation.Current.Value?.DirectoryPath ?? Path.Combine(Paths.ProductRoot, "debug-runs", "input-releases");
        ColdCheckpoint.Restrict(directory);
        var path = Path.Combine(directory, "release-" + Guid.NewGuid().ToString("N") + ".json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string? session = expectedSession;
        InputReleaseEvidence result;
        try
        {
            session ??= Transport.Session;
            await Transport.ReleaseAllAsync(timeout.Token, session);
            result = new("acknowledged", true, session, DateTimeOffset.UtcNow, Transport.ActiveTouchIds, null, path);
        }
        catch (Exception e) when (e is DebugException or RpcException or OperationCanceledException)
        {
            result = new("unverified", false, session, DateTimeOffset.UtcNow, Transport.ActiveTouchIds, DebugReply.Failure(e).Error!.Code, path);
            try
            {
                Instance.RequireStopped();
                result = result with { State = "instance_stopped", RemainingOwnedSlots = [], ErrorCode = null };
            }
            catch (DebugException) { /* An unreachable live instance is not proof that contacts are gone. */ }
        }
        await File.WriteAllTextAsync(path, DebugJson.Write(result), CancellationToken.None);
        _lastRelease = result;
        return result;
    }
    public async Task<(bool Boot, bool Awake, bool Locked, string? Foreground, int Rotation)> StateAsync(CancellationToken ct, bool force = false)
    {
        var host = Instance.Require(force);
        var session = $"{host.ProcessId}:{host.StartedAtUtcTicks}";
        await _stateGate.WaitAsync(ct);
        try
        {
            if (!force && _cachedState is { } cached && _cachedStateSession == session && Stopwatch.GetElapsedTime(_stateAt).TotalMilliseconds < 500) return cached;
            var state = await ReadStateAsync(ct);
            _cachedState = state; _cachedStateSession = session; _stateAt = Stopwatch.GetTimestamp();
            return state;
        }
        finally { _stateGate.Release(); }
    }
    private async Task<(bool Boot, bool Awake, bool Locked, string? Foreground, int Rotation)> ReadStateAsync(CancellationToken ct)
    {
        var text = await ShellAsync("getprop sys.boot_completed; dumpsys power | grep mWakefulness; dumpsys window displays; dumpsys window policy", false, ct, strict: false);
        var foreground = Regex.Match(text, @"mCurrentFocus=Window\{[^\r\n]*?\s([a-zA-Z][\w.]+)/(\S+)");
        if (!foreground.Success) foreground = Regex.Match(text, @"mFocusedApp=.*?\s([a-zA-Z][\w.]+)/");
        var rotation = Regex.Match(text, @"mRotation=ROTATION_(\d+)");
        var degrees = rotation.Success ? int.Parse(rotation.Groups[1].Value) : -1;
        return (text.StartsWith("1"), text.Contains("mWakefulness=Awake"),
            text.Contains("mShowingLockscreen=true") || text.Contains("showing=true") || text.Contains("isStatusBarKeyguard=true"),
            foreground.Success ? foreground.Groups[1].Value : null, degrees);
    }
    public async Task<PreviewFrame> PreviewAsync(CancellationToken ct)
    {
        await _captureGate.WaitAsync(ct);
        try { return await PreviewCoreAsync(ct); }
        finally { _captureGate.Release(); }
    }
    private async Task<PreviewFrame> PreviewCoreAsync(CancellationToken ct)
    {
        var host = Instance.Require(); var state = await StateAsync(ct);
        var frame = await Transport.PreviewAsync(ct);
        var bytes = frame.Image.Image_.Memory; var size = PngSize(bytes.Span);
        if (bytes.Length > 8 * 1024 * 1024) throw new IOException("预览数据超限。");
        var after = Instance.Require();
        if (host.ProcessId != after.ProcessId || host.StartedAtUtcTicks != after.StartedAtUtcTicks) throw new DebugException("stale_observation", "会话已变化。");
        return new(new($"{host.ProcessId}:{host.StartedAtUtcTicks}", size.Width, size.Height, frame.Width, frame.Height,
            state.Rotation, (int)frame.Image.Format.Rotation.Rotation_ * 90, state.Foreground, state.Awake, state.Locked, DateTimeOffset.UtcNow, bytes.Length), bytes);
    }
    public static (int Width, int Height) PngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new IOException("截图不是有效 PNG。");
        var w = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)); var h = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        if (w < 1 || h < 1 || w > 16384 || h > 16384) throw new IOException("截图尺寸无效。");
        return (w, h);
    }
    public async Task<ScreenObservation> ScreenshotAsync(string? directory, CancellationToken ct)
    {
        await _captureGate.WaitAsync(ct);
        try { return await ScreenshotCoreAsync(directory, ct); }
        finally { _captureGate.Release(); }
    }
    private async Task<ScreenObservation> ScreenshotCoreAsync(string? directory, CancellationToken ct)
    {
        var host = Instance.Require(); var state = await StateAsync(ct, force: true);
        var revision = Interlocked.Read(ref _pageRevision);
        var appPid = state.Foreground is null ? null : (await ShellAsync("pidof " + Q(state.Foreground) + " || true", false, ct)).Trim();
        ReadOnlyMemory<byte> png; var backend = "emulator-grpc"; var rotation = state.Rotation; var imageRotation = rotation; bool? blank = null;
        try { var image = await Transport.ScreenshotAsync(ct); png = image.Image_.Memory; imageRotation = (int)image.Format.Rotation.Rotation_ * 90; blank = await Transport.IsBlankAsync(ct); }
        catch (Exception e) when (e is RpcException or DebugException { Code: "grpc_unavailable" })
        { backend = "adb-exec-out"; png = await BinaryProcess.RunAsync(AndroidCommandFactory.Adb(Layout, Options, "exec-out", "screencap", "-p"), 64 * 1024 * 1024, ct); }
        var size = PngSize(png.Span);
        var after = Instance.Require();
        if (after.ProcessId != host.ProcessId || after.StartedAtUtcTicks != host.StartedAtUtcTicks) throw new DebugException("stale_observation", "截图期间会话发生变化。");
        var afterState = await StateAsync(ct, force: true);
        var afterPid = state.Foreground is null ? null : (await ShellAsync("pidof " + Q(state.Foreground) + " || true", false, ct)).Trim();
        if (afterState.Foreground != state.Foreground || afterState.Rotation != state.Rotation || afterPid != appPid || revision != Interlocked.Read(ref _pageRevision))
            throw new DebugException("stale_observation", "截图期间应用、旋转或输入操作发生变化，请重新观察。");
        directory ??= NewRecord("screen"); Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N"); var path = Path.Combine(directory, id + ".png");
        await using (var file = File.Create(path)) await file.WriteAsync(png, ct);
        var result = new ScreenObservation(id, $"{host.ProcessId}:{host.StartedAtUtcTicks}", path, backend, size.Width, size.Height,
            rotation, 0, state.Foreground, state.Boot, state.Awake, state.Locked, DateTimeOffset.UtcNow, imageRotation, blank,
            appPid, revision);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".json"), DebugJson.Write(result), ct);
        lock (_observations) { if (_observations.Count >= 64) _observations.Remove(_observations.Keys.First()); _observations[id] = result; }
        return result;
    }
    public static void ValidateFrames(InputFrame[] frames, ScreenObservation observation)
    {
        if (frames.Length is < 1 or > 10000 || frames[0].AtMs < 0 || frames[^1].AtMs > 120000)
            throw new ArgumentException("输入序列必须为 1–10000 帧，时长最多 120 秒。");
        var previous = -1;
        foreach (var frame in frames)
        {
            if (frame.AtMs < previous || frame.Touches.Length > 10 || frame.Touches.Select(t => t.Id).Distinct().Count() != frame.Touches.Length)
                throw new ArgumentException("输入时间顺序或触点编号无效。");
            foreach (var point in frame.Touches)
                if (point.Id is < 0 or > 9 || point.Pressure is < 0 or > 1024 || point.X < 0 || point.Y < 0 || point.X >= observation.Width || point.Y >= observation.Height)
                    throw new ArgumentException("触点超出已观察的屏幕边界。");
            previous = frame.AtMs;
        }
    }
    public static TouchPoint ToNativeTouch(TouchPoint point, ScreenObservation observation) => observation.ImageRotation switch
    {
        90 => point with { X = observation.Height - 1 - point.Y, Y = point.X },
        180 => point with { X = observation.Width - 1 - point.X, Y = observation.Height - 1 - point.Y },
        270 => point with { X = point.Y, Y = observation.Width - 1 - point.X },
        0 => point,
        _ => throw new DebugException("stale_observation", "未知图像旋转，拒绝输入。")
    };
    public async Task<object> InputAsync(string observationId, InputFrame[] frames, CancellationToken ct, long? startAtQpc = null)
    {
        InputClock.ResolveStart(startAtQpc, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        Instance.Require(force: true);
        await StateAsync(ct, force: true);
        ScreenObservation old;
        lock (_observations) old = _observations.GetValueOrDefault(observationId) ?? throw new DebugException("stale_observation", "请先获取新截图。");
        ValidateFrames(frames, old);
        var dir = NewRecord("input"); var fresh = await ScreenshotAsync(dir, ct);
        if (fresh.Session != old.Session || fresh.Width != old.Width || fresh.Height != old.Height || fresh.Rotation != old.Rotation || fresh.ImageRotation != old.ImageRotation || fresh.Foreground != old.Foreground ||
            old.AppPid is not null && fresh.AppPid != old.AppPid)
            throw new DebugException("stale_observation", "会话、分辨率、旋转或前台应用发生变化；请使用新截图坐标。");
        if (!fresh.BootCompleted || !fresh.Awake || fresh.Locked || fresh.Blank == true) throw new DebugException("screen_not_ready", "安卓未启动完成、息屏、锁屏或截图为疑似空白画面。");
        if (string.IsNullOrWhiteSpace(fresh.Foreground) || string.IsNullOrWhiteSpace(fresh.AppPid))
            throw new DebugException("screen_not_ready", "未确认当前前台应用及进程，拒绝向未知页面输入。");
        Progress.Value?.Invoke(new { stage = "preparing_input", directory = dir, session = fresh.Session, pid = fresh.AppPid });
        ct.ThrowIfCancellationRequested();
        var initialRelease = await ReleaseAsync(fresh.Session);
        if (!initialRelease.Acknowledged) throw new DebugException("input_release_unverified", "输入前未确认旧触点释放。", "preparing_input", initialRelease.EvidencePath);
        ct.ThrowIfCancellationRequested();
        var startTimestamp = InputClock.ResolveStart(startAtQpc, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        var clockFrequency = Stopwatch.Frequency;
        var timings = new List<InputTiming>();
        Exception? inputError = null;
        InputReleaseEvidence? cleanup = null;
        Progress.Value?.Invoke(new { stage = "sending_input", directory = dir, session = fresh.Session });
        try
        {
            for (var i = 0; i < frames.Length; i++)
            {
                while (InputClock.MillisecondsSince(startTimestamp) < frames[i].AtMs)
                {
                    ct.ThrowIfCancellationRequested();
                    var remaining = frames[i].AtMs - InputClock.MillisecondsSince(startTimestamp);
                    if (remaining > 18) await Task.Delay(TimeSpan.FromMilliseconds(remaining - 17), ct);
                    else Thread.SpinWait(128);
                }
                ct.ThrowIfCancellationRequested();
                // Each message has a finite lifetime. Finally/reconnect releases all product-owned slots.
                var dispatch = await Transport.SendAsync(frames[i].Touches.Select(p => ToNativeTouch(p, fresh)), ct, fresh.Session);
                var sent = (dispatch.SentTimestamp - startTimestamp) * 1000d / clockFrequency;
                timings.Add(new(i, frames[i].AtMs, sent, sent - frames[i].AtMs, dispatch.SentTimestamp, dispatch.AcknowledgedTimestamp));
            }
        }
        catch (Exception error)
        {
            inputError = error;
            var detail = DebugReply.Failure(error).Error!;
            throw new DebugException(detail.Code, detail.Message, detail.Stage ?? "sending_input", Path.Combine(dir, "input.json"), error);
        }
        finally
        {
            try { cleanup = await ReleaseAsync(fresh.Session); }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(dir, "input.json"), DebugJson.Write(new
                {
                    observation = old,
                    frames,
                    timings,
                    startTimestamp,
                    clockFrequency,
                    cancelled = ct.IsCancellationRequested,
                    cleanup,
                    failure = inputError is null ? null : DebugReply.Failure(inputError).Error
                }), CancellationToken.None);
            }
        }
        if (cleanup?.Acknowledged != true) throw new DebugException("input_release_unverified", "输入已结束，但未确认触点释放；请检查恢复记录并重新观察。", "releasing_input", cleanup?.EvidencePath);
        Progress.Value?.Invoke(new { stage = "verifying_after_input", directory = dir, session = fresh.Session, pid = fresh.AppPid });
        return new
        {
            directory = dir,
            timings,
            startTimestamp,
            clockFrequency,
            clock = "Windows QPC / Stopwatch; RPC call and acknowledgement, not game response",
            cleanup,
            after = await ScreenshotAsync(dir, ct)
        };
    }
    public async Task<object> ExecuteAsync(DebugRequest request, CancellationToken ct)
    {
        if (request.SchemaVersion != 1) throw new ArgumentException("未知协议版本。");
        var package = request.Text("package");
        if (request.Command is "launch" or "force-stop" or "uninstall" or "app.observe" or "app.page.annotate" or "logs" or "metrics" or "frames.sample")
            package = RequirePackage(request);
        if (request.Command is "input" or "key" or "launch" or "force-stop" or "install" or "stop" or "root-shell" or "shell")
            Interlocked.Increment(ref _pageRevision);
        switch (request.Command)
        {
            case "session.observe": return await SessionObservationAsync(package, request.Flag("refresh"), ct);
            case "app.page.annotate": return await ObservePageAsync(request, ct);
            case "licenses":
                var assembly = typeof(AndroidDebugService).Assembly;
                return assembly.GetManifestResourceNames().Where(n => n.Contains("Licenses.")).ToDictionary(n => n,
                    n => { using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!); return reader.ReadToEnd(); });
            case "status": return await StatusAsync(ct);
            case "memory.snapshot": return ProcessMemory.Read(Paths);
            case "grpc.audit": return await Transport.AuditAuthenticationAsync(ct);
            case "schema":
                return new
                {
                    protocolVersion = 1,
                    commands = DebugCommandCatalog.Commands,
                    inputLeaseSeconds = 5,
                    inputCoordinates = "原始 PNG 像素",
                    maxTouches = 10,
                    inputScheduling = new
                    {
                        optionalStart = "arguments.startAtQpc: Int64 Windows QPC timestamp",
                        maximumFutureSeconds = 120,
                        missedSchedule = "input_schedule_missed",
                        sentTimestampMeaning = "RPC invocation, not game response",
                        clockFrequencyInResult = true
                    },
                    request = DebugProtocolSchema.Request,
                    response = DebugProtocolSchema.Response,
                    jobStates = DebugProtocolSchema.JobStates,
                    maxRequestBytes = 1024 * 1024,
                    maxInlineJobResultBytes = StoredJobResult.MaxInlineBytes,
                    jobResultsOnDisk = true,
                    testStepResultsOnDisk = true
                };
            case "runtime.inspect":
                var requestedProfile = new RuntimeProfileStore(Paths).Read();
                DisplayTelemetry? observedDisplay = null;
                if (await _controller.GetStatusAsync(ct) == VmStatus.Running)
                {
                    var displayDump = await ShellAsync("dumpsys display", false, ct);
                    var surfaceDump = await ShellAsync("dumpsys SurfaceFlinger", false, ct);
                    observedDisplay = DisplayTelemetry.Parse(displayDump, surfaceDump);
                }
                var memoryCapacity = HostMemory.Read();
                return new
                {
                    requested = requestedProfile,
                    observed = observedDisplay,
                    host = memoryCapacity,
                    observedMemory = observedDisplay is null ? null : await ReadGuestMemoryAsync(requestedProfile, ct),
                    startAdmission = observedDisplay is null ? VmMemoryPolicy.Assess(requestedProfile.MemoryMb, memoryCapacity, requestedProfile.StartAvailableMb, requestedProfile.LowRam) : null,
                    requestedRefreshConfirmed = observedDisplay?.ConfirmsRequestedRate(requestedProfile.RefreshRate) ?? false
                };
            case "runtime.configure":
                var requested = request.Value<RuntimeProfile>("profile") ?? throw new ArgumentException("缺少 profile。");
                var hostCapacity = HostMemory.Read();
                return await new RuntimeProfileStore(Paths).ApplyAsync(requested, Instance.RequireStopped,
                    hostCapacity.TotalMb, hostCapacity.LogicalCores, ct);
            case "display.desktop": return await ApplyDesktopAppearanceAsync(request.Flag("enabled"), ct);
            case "capabilities":
                return new
                {
                    version = "0.5.0",
                    protocol = 1,
                    ownedAvdOnly = true,
                    maxTouches = 10,
                    inputBackend = "authenticated-loopback-grpc",
                    screenshotFallback = "binary-adb",
                    videoAudio = false,
                    jobResultsOnDisk = true,
                    requestIds = true,
                    jobRecovery = true,
                    toolOutputArtifacts = true,
                    maxInlineJobResultBytes = StoredJobResult.MaxInlineBytes,
                    commands = DebugCommandCatalog.Commands.Select(command => command.Name).ToArray()
                };
            case "start": await StartAsync(ct); return await StatusAsync(ct);
            case "stop":
                if (request.Text("expectedSession") is { Length: > 0 } expectedSession)
                {
                    var target = Instance.Require(force: true);
                    if (expectedSession != $"{target.ProcessId}:{target.StartedAtUtcTicks}")
                        throw new DebugException("instance_mismatch", "运行会话已改变，取消针对旧会话的停止操作。");
                }
                await StopAsync(ct); return new { stopped = true };
            case "screen": return await ScreenshotAsync(null, ct);
            case "preview": return await PreviewAsync(ct);
            case "preview.benchmark": return await PreviewBenchmarkAsync(request.Number("frames", 20), ct);
            case "frames.sample": return await SampleFrameTimingsAsync(package, request.Number("seconds", 15), ct);
            case "window.focus": return OwnedVmWindow.Show(Instance);
            case "input": return await InputAsync(request.Text("observation"), request.Value<InputFrame[]>("frames") ?? [], ct, request.Value<long?>("startAtQpc"));
            case "release":
                var released = await ReleaseAsync();
                if (!released.Acknowledged && released.State != "instance_stopped") throw new DebugException("input_release_unverified", "未确认触点释放；" + released.ErrorCode, "releasing_input", released.EvidencePath);
                return new { released = true, evidence = released, guestStateVerified = false };
            case "wake": await ShellAsync("input keyevent KEYCODE_WAKEUP; wm dismiss-keyguard", false, ct); var state = await StateAsync(ct); return new { awake = state.Awake, locked = state.Locked, bootCompleted = state.Boot, foreground = state.Foreground };
            case "key": var key = request.Text("key"); if (!Regex.IsMatch(key, "^KEYCODE_[A-Z0-9_]+$")) throw new ArgumentException("请使用 Android KEYCODE 名称。"); return await ShellAsync("input keyevent " + key, false, ct);
            case "clipboard": return new { text = await Transport.ClipboardAsync(request.Arguments?.ContainsKey("text") == true ? request.Text("text") : null, ct) };
            case "shell": case "root-shell": return new { stdout = await ShellAsync(request.Text("script"), request.Command == "root-shell", ct) };
            case "apps": return await _controller.ListThirdPartyPackagesAsync(ct);
            case "apps.list": return await ListApplicationsAsync(request, ct);
            case "apps.resolve": return await ResolveApplicationAsync(request, ct);
            case "users.list": return await ListAndroidUsersAsync(ct);
            case "files.roots": return await FileRootsAsync(request, ct);
            case "files.browse": return await BrowseDirectoryAsync(request, ct);
            case "files.stat": return await ObserveFileAsync(request, ct);
            case "files.transfer.plan": return await PlanFileTransferAsync(request, ct);
            case "files.transfer.inspect": return await ReadTransferPlanAsync(request, ct);
            case "launch": return await LaunchAsync(request, ct);
            case "app.observe": return await ObserveApplicationAsync(package, NewRecord("app-observation"), ct);
            case "force-stop": AndroidPackageName.Parse(package); Instance.Require(); await _controller.ForceStopPackageAsync(package, ct); return new { package, stopped = true };
            case "uninstall":
                AndroidPackageName.Parse(package); Instance.Require(force: true);
                if (!request.Flag("confirm")) throw new DebugException("confirmation_required", "卸载会删除应用及数据，需要明确确认。");
                if (package == "com.topjohnwu.magisk" || !(await _controller.ListThirdPartyPackagesAsync(ct)).Contains(package))
                    throw new DebugException("protected_app", "此入口仅卸载普通第三方应用；系统和 Root 维护组件受保护。");
                await _controller.UninstallPackageAsync(package, ct); return new { package, uninstalled = true };
            case "install": return await InstallAsync(request.Text("path"), ct);
            case "apk.inspect": return ApkMetadata.Read(request.Text("path"));
            case "performance.set":
                Instance.RequireStopped();
                if (!Enum.TryParse<PerformanceProfile>(request.Text("profile"), true, out var profile)) throw new ArgumentException("profile 为 Stable 或 HighPerformance。");
                await new PerformanceProfileService(Options, Paths.ProductRoot).ApplyAsync(profile, ct); return new { profile = profile.ToString(), nextStart = true };
            case "checkpoint.create": await StopAsync(ct); return await Checkpoints.CreateAsync(ct);
            case "checkpoint.list": return Checkpoints.List();
            case "checkpoint.restore":
                await StopAsync(ct); var restored = await Checkpoints.RestoreAsync(request.Text("id"), ct);
                try
                {
                    await StartAsync(ct, verifyingRestore: true);
                    if (!(await ShellAsync("id", true, ct)).Contains("uid=0")) throw new DebugException("permission_denied", "恢复后的 Root 验证失败。");
                    var verification = await StatusAsync(ct);
                    var packages = await _controller.ListThirdPartyPackagesAsync(ct);
                    Checkpoints.CompleteRestore();
                    return new { restored = restored with { RequiresColdBootVerification = false }, verification, packages };
                }
                catch
                {
                    using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await StopAsync(rollbackTimeout.Token); Checkpoints.Rollback(restored); throw;
                }
            case "checkpoint.recover": await StopAsync(ct); return await Checkpoints.RecoverPendingAsync(ct);
            case "files.list": case "files.pull": case "files.push": case "files.diff": case "files.sync": case "files.export": return await FilesAsync(request, ct);
            case "logs": return await LogsAsync(package, Math.Clamp(request.Number("seconds", 30), 1, 3600), ct);
            case "metrics": return await MetricsAsync(package, ct);
            case "record": case "trace": return await RecordAsync(request.Command, Math.Clamp(request.Number("seconds", 30), 1, 180), ct);
            case "test": return await TestAsync(request, ct);
            default: throw new ArgumentException("未知命令：" + request.Command);
        }
    }
    public void Dispose() { Transport.Dispose(); Instance.Dispose(); _stateGate.Dispose(); _captureGate.Dispose(); _summaryGate.Dispose(); _catalogGate.Dispose(); _helperGate.Dispose(); }
}
