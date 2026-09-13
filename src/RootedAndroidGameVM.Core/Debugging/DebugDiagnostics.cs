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
    public async Task<object> ImportAsync(string path, bool reload, CancellationToken ct)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".msp" or ".mcz")) throw new ArgumentException("Malody 模板只接受 .msp 和 .mcz。");
        IO.ImportArchivePolicy.Validate(path);
        var dir = NewRecord("malody-import");
        var remote = "/sdcard/Android/data/" + MalodyPackage + "/files/rgvm-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path).ToLowerInvariant();
        if (reload) await _controller.ForceStopPackageAsync(MalodyPackage, ct);
        var running = (await ShellAsync("pidof " + Q(MalodyPackage) + " || true", false, ct)).Trim();
        if (running.Length == 0)
        {
            await _controller.LaunchPackageAsync(MalodyPackage, ct);
            // Cold-start Intent data can be lost while Unity initializes. Send VIEW only after a visible frame.
            await Task.Delay(5000, ct);
            var ready = false;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var observation = await ScreenshotAsync(dir, ct);
                if (observation.Foreground == MalodyPackage && observation.Blank == false) { ready = true; break; }
                await Task.Delay(1000, ct);
            }
            if (!ready) throw new DebugException("app_not_ready", "Malody 尚未出现可观察画面，导入尚未发送。");
        }
        await ShellAsync("test -d " + Q(remote[..remote.LastIndexOf('/')]), false, ct);
        await AdbAsync(["push", Path.GetFullPath(path), remote], ct);
        var localHash = (await ColdCheckpoint.DigestAsync(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFullPath(path), ct)).Sha256;
        var remoteHash = (await ShellAsync("sha256sum " + Q(remote), false, ct)).Split(' ')[0];
        if (!localHash.Equals(remoteHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("传入文件散列不一致。");
        // Malody 6.x consumes a raw absolute Intent data path. file:// produces a wrong /file:/ path.
        var intent = await AdbAsync(["shell", "am", "start", "-n", MalodyPackage + "/.MainActivity", "-a", "android.intent.action.VIEW", "-d", remote, "-t", "application/octet-stream"], ct);
        await Task.Delay(2000, ct);
        var imported = await VerifyImportAsync(path, remote, ct);
        var evidence = await ScreenshotAsync(dir, ct);
        var result = new
        {
            directory = dir,
            sourceSha256 = localHash,
            remote,
            reload,
            intent,
            screen = evidence,
            transferVerified = true,
            imported,
            importConfirmed = imported.Verified,
            status = imported.Verified ? "imported_activation_requires_app_confirmation" : "awaiting_app_confirmation",
            instructions = "文件散列验证只证明 Malody 已解包；请在应用内确认启用，授权加载与可游玩状态仍须测试。"
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "import.json"), DebugJson.Write(result), ct); return result;
    }
    public sealed record ImportVerification(bool Verified, string Target, int VerifiedFiles, int ExpectedFiles, string? Reason);
    private async Task<ImportVerification> VerifyImportAsync(string source, string remote, CancellationToken ct)
    {
        var kind = Path.GetExtension(source).Equals(".msp", StringComparison.OrdinalIgnoreCase) ? "skin" : "chart";
        var target = "/sdcard/Android/data/" + MalodyPackage + "/files/" + kind + "/" + Path.GetFileNameWithoutExtension(remote);
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(source);
            var files = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToArray();
            if (files.Length > 4096 || files.Sum(e => e.Length) > 512L * 1024 * 1024) return new(false, target, 0, files.Length, "archive_limit");
            var expected = new Dictionary<string, string>();
            foreach (var file in files)
            {
                RemotePath("external", MalodyPackage, file.FullName);
                using var stream = file.Open(); expected[file.FullName] = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            }
            var verified = 0;
            foreach (var attempt in Enumerable.Range(0, 10))
            {
                var result = await ShellAsync("test -d " + Q(target) + " && find " + Q(target) + " -type f -exec sha256sum {} \\; || true", false, ct);
                var actual = result.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 66)
                    .Select(l => (Hash: l[..64], Path: l[66..])).Where(v => v.Path.StartsWith(target + "/", StringComparison.Ordinal))
                    .ToDictionary(v => v.Path[(target.Length + 1)..], v => v.Hash);
                verified = expected.Count(e => actual.GetValueOrDefault(e.Key) == e.Value);
                if (verified == expected.Count && verified > 0) return new(true, target, verified, expected.Count, null);
                await Task.Delay(1000, ct);
            }
            return new(false, target, verified, expected.Count, "not_all_entries_observed_may_be_duplicate_import");
        }
        catch (InvalidDataException) { return new(false, target, 0, 0, "unsupported_container_requires_manual_verification"); }
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
                var bytes = await BinaryProcess.RunAsync(AndroidCommandFactory.Adb(Layout, Options, "exec-out", "atrace", "--async_stop", "-z"), 64 * 1024 * 1024, ct);
                var trace = Path.Combine(dir, "system.atrace"); await File.WriteAllBytesAsync(trace, bytes, ct); return new { directory = dir, trace };
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
                results.Add(new { index = i, started, ended = DateTimeOffset.UtcNow, result });
                await File.WriteAllTextAsync(Path.Combine(dir, "results.json"), DebugJson.Write(results), ct);
            }
            return new { directory = dir, completed = true, results };
        }
        catch (Exception e) { await File.WriteAllTextAsync(Path.Combine(dir, "failure.json"), DebugJson.Write(DebugReply.Failure(e)), CancellationToken.None); throw; }
        finally { await ReleaseAsync(); }
    }
}
