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
        await _controller.InstallApkAsync(path, ct);
        await _controller.LaunchPackageAsync(apk.Package, ct);
        await Task.Delay(3000, ct);
        var pid = (await ShellAsync("pidof " + Q(apk.Package) + " || true", false, ct)).Trim();
        if (pid.Length == 0) throw new DebugException("app_exited", "APK 已安装但启动后退出；需检查 ABI 兼容和崩溃日志。");
        var dir = NewRecord("install");
        var result = new { apk, pid, launchObserved = true, compatibility = "process_running_requires_functional_validation", screen = await ScreenshotAsync(dir, ct) };
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var process = Process.Start(ProcessStartInfoFactory.Create(AndroidCommandFactory.Adb(Layout, Options, "logcat", "-v", "epoch", "-T", "1")))!;
        using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stderr = BinaryProcess.ReadBoundedAsync(process.StandardError.BaseStream, 1024 * 1024, timeout.Token);
        await using var writer = new StreamWriter(path); long bytes = 0; var lost = false; var pids = new HashSet<string>(); var nextPidCheck = DateTimeOffset.MinValue;
        var count = 0;
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= nextPidCheck)
                {
                    var current = (await ShellAsync("pidof " + Q(package) + " || true", false, timeout.Token)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    if (!current.SetEquals(pids)) { await writer.WriteLineAsync(DebugJson.Write(new { type = "pid_change", previous = pids, current, at = DateTimeOffset.UtcNow })); pids = current; }
                    nextPidCheck = DateTimeOffset.UtcNow.AddSeconds(1);
                }
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token); if (line is null) break;
                var match = Regex.Match(line, @"^\s*(\d+\.\d+)\s+(\d+)\s+(\d+)\s+([A-Z])\s+(.*)$");
                var important = Regex.IsMatch(line, "ANR in |FATAL EXCEPTION|Fatal signal|am_crash|am_anr", RegexOptions.IgnoreCase) || LogClassification.IsLossMarker(line);
                if (!important && (!match.Success || !pids.Contains(match.Groups[2].Value))) continue;
                lost |= LogClassification.IsLossMarker(line);
                var item = DebugJson.Write(new { type = LogClassification.IsLossMarker(line) ? "loss" : LogClassification.Category(line), source = "logcat", at = DateTimeOffset.UtcNow, raw = line });
                bytes += System.Text.Encoding.UTF8.GetByteCount(item);
                if (bytes > 64L * 1024 * 1024) { lost = true; await writer.WriteLineAsync(DebugJson.Write(new { type = "truncated", reason = "64MiB_limit" })); break; }
                await writer.WriteLineAsync(item); await writer.FlushAsync(timeout.Token); count++;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            try { await stderr; } catch (OperationCanceledException) { }
            await writer.WriteLineAsync(DebugJson.Write(new { type = "end", cancelled = ct.IsCancellationRequested, lostOrTruncated = lost, lossDetection = "logcat_reported_only", count }));
        }
        return new { directory, path, count, lostOrTruncated = lost, cancelled = ct.IsCancellationRequested };
    }
    public async Task<object> RecordAsync(string kind, int seconds, CancellationToken ct)
    {
        var gate = kind == "trace" ? _traceGate : _recordGate;
        if (!await gate.WaitAsync(0, ct)) throw new DebugException("busy", "同类采集任务正在运行。");
        try { return await RecordCoreAsync(kind, seconds, ct); }
        finally { gate.Release(); }
    }
    private async Task<object> RecordCoreAsync(string kind, int seconds, CancellationToken ct)
    {
        var dir = NewRecord(kind); var remote = "/data/local/tmp/rgvm-" + Guid.NewGuid().ToString("N") + ".mp4";
        var pidFile = remote + ".pid";
        if (kind == "trace")
        {
            await ShellAsync("atrace --async_start -b 8192 gfx view wm am sched", false, ct);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                var trace = Path.Combine(dir, "system.atrace");
                var bytes = await BinaryProcess.RunToFileAsync(AndroidCommandFactory.Adb(Layout, Options, "exec-out", "atrace", "--async_stop", "-z"), trace, 64 * 1024 * 1024, ct);
                return new { directory = dir, trace, bytes };
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await ShellAsync("atrace --async_stop >/dev/null", false, cleanup.Token); }
                catch { await File.WriteAllTextAsync(Path.Combine(dir, "cleanup-required.txt"), "atrace --async_stop"); }
            }
        }
        try
        {
            await ShellAsync("screenrecord --time-limit " + seconds + " " + Q(remote) + " & p=$!; echo $p > " + Q(pidFile) + "; wait $p", false, ct);
            var path = Path.Combine(dir, "screen-no-audio.mp4"); await AdbAsync(["pull", remote, path], ct);
            return new { directory = dir, path, includesAudio = false, requestedSeconds = seconds };
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await ShellAsync("p=$(cat " + Q(pidFile) + " 2>/dev/null); case \"$p\" in ''|*[!0-9]*) ;; *) " +
                    "if test -r /proc/$p/cmdline && tr '\\000' ' ' < /proc/$p/cmdline | grep -F " + Q(remote) + " >/dev/null; then kill -INT $p; fi;; esac; rm -f " + Q(remote) + " " + Q(pidFile), false, cleanup.Token);
            }
            catch { await File.WriteAllTextAsync(Path.Combine(dir, "cleanup-required.txt"), remote); }
        }
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
