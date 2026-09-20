using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    private readonly SemaphoreSlim _recordGate = new(1, 1);
    private readonly SemaphoreSlim _traceGate = new(1, 1);
    public async Task<object> InstallAsync(string path, CancellationToken ct)
    {
        var apk = ApkMetadata.Read(path); AndroidPackageName.Parse(apk.Package); Instance.Require();
        var installed = await ShellAsync("dumpsys package " + Q(apk.Package), false, ct);
        var currentVersion = Regex.Match(installed, @"versionCode=(\d+)");
        if (currentVersion.Success && long.Parse(currentVersion.Groups[1].Value) > apk.VersionCode)
            return new { apk, action = "kept_newer_installed_version", installedVersion = currentVersion.Groups[1].Value };
        var dir = NewRecord("install");
        Progress.Value?.Invoke(new { stage = "installing", directory = dir, package = apk.Package });
        await _controller.InstallApkAsync(path, ct);
        var launch = await LaunchProcessAsync(apk.Package, dir, ct);
        var result = new
        {
            apk,
            pid = launch.Pid,
            stage = "process_observed",
            interactiveReady = false,
            launchObserved = true,
            launchDiagnostic = launch.LaunchDiagnostic,
            compatibility = "process_running_requires_functional_validation",
            screen = await ScreenshotAsync(dir, ct)
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "install.json"), DebugJson.Write(result), ct); return result;
    }
    public async Task<object> MetricsAsync(string package, CancellationToken ct)
    {
        AndroidPackageName.Parse(package);
        var pid = (await ShellAsync("pidof " + Q(package) + " || true", false, ct)).Trim();
        if (pid.Length == 0) throw new DebugException("app_exited", "应用未运行。");
        var memory = await ShellAsync("dumpsys meminfo " + Q(package), false, ct);
        var cpu = await ShellAsync("dumpsys cpuinfo", false, ct);
        var frames = await ShellAsync("dumpsys gfxinfo " + Q(package) + " framestats", false, ct);
        var pss = Regex.Match(memory, @"TOTAL PSS:\s+(\d+)");
        var cpuLine = cpu.Split('\n').FirstOrDefault(l => l.Contains("/" + package + ":"));
        var rendered = Regex.Match(frames, @"Total frames rendered:\s*(\d+)");
        return new
        {
            package,
            pid,
            totalPssKb = pss.Success ? (long?)long.Parse(pss.Groups[1].Value) : null,
            cpu = cpuLine?.Trim(),
            frameTimingAvailable = rendered.Success && long.Parse(rendered.Groups[1].Value) > 0,
            frameTimingNote = "Unity SurfaceView 可能不提供 gfxinfo 帧时序；不可用不表示零帧或零耗时。",
            memoryRaw = memory,
            frameTimingRaw = frames,
            sampledAt = DateTimeOffset.UtcNow
        };
    }
    public async Task<object> LogsAsync(string package, int seconds, CancellationToken ct)
    {
        AndroidPackageName.Parse(package); Instance.Require();
        var directory = NewRecord("logs"); var path = Path.Combine(directory, "events.ndjson");
        var script = await OwnedGuestScriptAsync("logcat -v epoch -T 1", ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        await using var writer = new StreamWriter(path); long bytes = 0; var lost = false; var pids = new HashSet<string>(); var nextPidCheck = DateTimeOffset.MinValue;
        var count = 0; var reason = "failed"; var rawPath = Path.Combine(directory, "stdout.log");
        try
        {
            await BinaryProcess.RunLinesToFileAsync(AndroidCommandFactory.Adb(Layout, Options, "shell", script), rawPath, 64L * 1024 * 1024, async line =>
            {
                if (timeout.IsCancellationRequested) return;
                if (DateTimeOffset.UtcNow >= nextPidCheck)
                {
                    var current = (await ShellAsync("pidof " + Q(package) + " || true", false, timeout.Token)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    if (!current.SetEquals(pids)) { await writer.WriteLineAsync(DebugJson.Write(new { type = "pid_change", previous = pids, current, at = DateTimeOffset.UtcNow })); pids = current; }
                    nextPidCheck = DateTimeOffset.UtcNow.AddSeconds(1);
                }
                var match = Regex.Match(line, @"^\s*(\d+\.\d+)\s+(\d+)\s+(\d+)\s+([A-Z])\s+(.*)$");
                var important = Regex.IsMatch(line, "ANR in |FATAL EXCEPTION|Fatal signal|am_crash|am_anr", RegexOptions.IgnoreCase) || LogClassification.IsLossMarker(line);
                if (!important && (!match.Success || !pids.Contains(match.Groups[2].Value))) return;
                lost |= LogClassification.IsLossMarker(line);
                var item = DebugJson.Write(new { type = LogClassification.IsLossMarker(line) ? "loss" : LogClassification.Category(line), source = "logcat", at = DateTimeOffset.UtcNow, raw = line });
                bytes += System.Text.Encoding.UTF8.GetByteCount(item);
                if (bytes > 64L * 1024 * 1024) throw new DebugException("output_limit", "日志达到64MiB上限。", "collecting_logs");
                await writer.WriteLineAsync(item); await writer.FlushAsync(timeout.Token); count++;
            }, timeout.Token);
            throw new DebugException("tool_exited_early", "logcat在采集时长结束前退出，日志可能不完整。", "collecting_logs", directory);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested) { reason = "duration_elapsed"; }
        catch (DebugException error) when (error.Code == "output_limit" && !ct.IsCancellationRequested)
        { lost = true; reason = "output_limit"; await writer.WriteLineAsync(DebugJson.Write(new { type = "truncated", reason = "64MiB_limit" })); }
        finally
        {
            await writer.WriteLineAsync(DebugJson.Write(new
            {
                type = "end",
                reason = ct.IsCancellationRequested ? "cancelled" : reason,
                cancelled = ct.IsCancellationRequested,
                lostOrTruncated = lost,
                lossDetection = "logcat_reported_only",
                count
            }));
        }
        ct.ThrowIfCancellationRequested();
        return new { directory, path, rawPath, count, lostOrTruncated = lost, cancelled = false, completionReason = reason };
    }
    public async Task<object> RecordAsync(string kind, int seconds, CancellationToken ct)
    {
        var gate = kind == "trace" ? _traceGate : _recordGate;
        if (!await gate.WaitAsync(0, ct)) throw new DebugException("busy", "同类采集任务正在运行。");
        try { return await RecordCoreAsync(kind, seconds, ct); }
        finally { try { await CompleteGuestToolsAsync(requireClean: false); } finally { gate.Release(); } }
    }
    private async Task<object> RecordCoreAsync(string kind, int seconds, CancellationToken ct)
    {
        var dir = NewRecord(kind); var session = CatalogSession();
        if (kind == "trace")
        {
            var state = (await ShellAsync("for p in /sys/kernel/tracing/tracing_on /sys/kernel/debug/tracing/tracing_on; do if test -f \"$p\"; then printf '%s ' \"$p\"; cat \"$p\"; break; fi; done", true, ct)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (state.Length != 2 || state[1] is not ("0" or "1")) throw new DebugException("trace_state_unknown", "未能读取内核追踪状态。", "starting_trace");
            if (state[1] == "1") throw new DebugException("busy", "已有内核追踪正在运行，未接管或停止它。", "starting_trace");
            await RegisterDiagnosticResourceAsync(new("trace", state[0]), ct);
            var trace = Path.Combine(dir, "system.atrace");
            var script = await OwnedGuestScriptAsync("atrace -t " + seconds + " -b 8192 -z gfx view wm am sched", ct);
            var bytes = await BinaryProcess.RunToArtifactAsync(AndroidCommandFactory.Adb(Layout, Options, "exec-out", script), trace, 64 * 1024 * 1024, ct);
            if (bytes == 0) throw new DebugException("trace_empty", "追踪工具没有生成数据。", "collecting_trace", trace);
            RequireCatalogSession(session); await CompleteGuestToolsAsync();
            return new { directory = dir, trace, bytes, kernelTracingStopped = true };
        }
        await OwnedGuestScriptAsync("true", ct);
        var remote = "/data/local/tmp/rgvm-record-" + _guestToolScope.Value!.Token + ".mp4";
        var path = Path.Combine(dir, "screen-no-audio.mp4");
        await RegisterDiagnosticResourceAsync(new("record", remote, path), ct);
        await ShellAsync("screenrecord --time-limit " + seconds + " " + Q(remote), false, ct);
        RequireCatalogSession(session); await CompleteGuestToolsAsync();
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new DebugException("recording_empty", "录屏工具未生成非空文件。", "verifying_record", dir);
        return new { directory = dir, path, includesAudio = false, requestedSeconds = seconds, fileVerified = true, mediaPlaybackVerified = false };
    }
    public async Task<object> TestAsync(DebugRequest request, CancellationToken ct)
    {
        var steps = request.Value<DebugRequest[]>("steps") ?? throw new ArgumentException("测试需要 steps 数组。");
        if (steps.Length > 1000 || steps.Any(s => s.Command is "test" or "checkpoint.restore" or "checkpoint.create")) throw new ArgumentException("测试步骤超限或包含嵌套测试/检查点。");
        var dir = NewRecord("test"); var results = new List<object>();
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.json"), DebugJson.Write(request), ct);
        try
        {
            for (var i = 0; i < steps.Length; i++)
            {
                var started = DateTimeOffset.UtcNow;
                var result = await ExecuteAsync(steps[i], ct);
                var resultPath = Path.Combine(dir, $"step-{i:D4}.json");
                await using (var output = File.Create(resultPath))
                    await JsonSerializer.SerializeAsync(output, result, DebugJson.Options, ct);
                results.Add(new { index = i, started, ended = DateTimeOffset.UtcNow, resultPath });
                await File.WriteAllTextAsync(Path.Combine(dir, "results.json"), DebugJson.Write(results), ct);
            }
            return new { directory = dir, completed = true, results };
        }
        catch (Exception e) { await File.WriteAllTextAsync(Path.Combine(dir, "failure.json"), DebugJson.Write(DebugReply.Failure(e)), CancellationToken.None); throw; }
        finally { await ReleaseAsync(); }
    }
}
