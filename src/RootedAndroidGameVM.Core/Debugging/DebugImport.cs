using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record ApplicationReadiness(string Package, string? Pid, string? Foreground, bool ActivityReady,
    bool InteractiveReady, string Stage, DateTimeOffset ObservedAt, string EvidencePath);

public sealed class ImportRecord
{
    public required string ImportId { get; init; }
    public required string Extension { get; init; }
    public required string SourceSha256 { get; init; }
    public required Dictionary<string, string> Expected { get; init; }
    public string? SourceMetadata { get; init; }
    public required string Session { get; set; }
    public string Stage { get; set; } = "prepared";
    public string? FailedStage { get; set; }
    public DebugError? Error { get; set; }
    public bool TransferVerified { get; set; }
    public int TransferCount { get; set; }
    public int TriggerCount { get; set; }
    public ApplicationReadiness? App { get; set; }
    public ImportVerification? Imported { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? RequestId { get; set; }
    public string? JobId { get; set; }
}

public sealed partial class AndroidDebugService
{
    public async Task<ApplicationReadiness> ObserveApplicationAsync(string package, string directory, CancellationToken ct)
    {
        Android.AndroidPackageName.Parse(package);
        var pid = (await ShellAsync("pidof " + Q(package) + " || true", false, ct)).Trim();
        if (DebugOperation.Current.Value is { } operation) operation.Pid = pid.Length == 0 ? null : pid;
        var state = await StateAsync(ct, force: true);
        var activity = await ShellAsync("dumpsys activity activities", false, ct);
        var path = Path.Combine(directory, "activity-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        await File.WriteAllTextAsync(path, activity, ct);
        var resumed = Regex.IsMatch(activity, @"(?:mResumedActivity|topResumedActivity)[^\r\n]*\b" + Regex.Escape(package) + @"/");
        var ready = pid.Length > 0 && resumed && state.Foreground == package && state.Boot && state.Awake && !state.Locked;
        return new(package, pid.Length == 0 ? null : pid, state.Foreground, ready, false,
            ready ? "activity_ready" : pid.Length > 0 ? "process_observed" : "process_absent", DateTimeOffset.UtcNow, path);
    }

    public async Task<object> ImportAsync(DebugRequest request, CancellationToken ct)
    {
        var id = request.Text("importId");
        var resume = id.Length > 0;
        if (!resume) id = Guid.NewGuid().ToString("N");
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new ArgumentException("importId 无效。");
        var directory = ColdCheckpoint.SafeChild(Path.Combine(Paths.ProductRoot, "debug-runs", "imports"), id);
        var statePath = Path.Combine(directory, "import.json");
        ImportRecord record;
        if (resume)
        {
            Storage.StoragePathPolicy.RejectReparsePoints(statePath);
            record = JsonSerializer.Deserialize<ImportRecord>(await File.ReadAllTextAsync(statePath, ct), DebugJson.Options)
                ?? throw new InvalidDataException("导入记录为空。");
            if (record.ImportId != id || record.Extension is not (".msp" or ".mcz")) throw new InvalidDataException("导入记录身份不符。");
        }
        else
        {
            var source = Path.GetFullPath(request.Text("path"));
            Storage.StoragePathPolicy.RejectReparsePoints(source);
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (extension is not (".msp" or ".mcz")) throw new ArgumentException("Malody 模板只接受 .msp 和 .mcz。");
            if (new FileInfo(source).Length > 512L * 1024 * 1024) throw new InvalidDataException("导入包超过512MiB。");
            IO.ImportArchivePolicy.Validate(source);
            Instance.Require();
            ColdCheckpoint.Restrict(directory);
            var snapshot = Path.Combine(directory, "source" + extension);
            File.Copy(source, snapshot);
            // Validate the immutable snapshot too, so a concurrently changed source cannot bypass validation.
            IO.ImportArchivePolicy.Validate(snapshot);
            using var archive = ZipFile.OpenRead(snapshot);
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            string? metadata = null;
            foreach (var file in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
            {
                using var data = file.Open();
                expected[file.FullName] = Convert.ToHexString(await SHA256.HashDataAsync(data, ct)).ToLowerInvariant();
                if (extension == ".msp" && file.FullName == "info.json" && file.Length <= 65536)
                { using var reader = new StreamReader(file.Open()); metadata = await reader.ReadToEndAsync(ct); }
            }
            if (expected.Count == 0) throw new InvalidDataException("导入包没有文件。");
            record = new() { ImportId = id, Extension = extension, Expected = expected, SourceMetadata = metadata,
                SourceSha256 = (await ColdCheckpoint.DigestAsync(directory, snapshot, ct)).Sha256, Session = Transport.Session };
        }
        var remote = "/sdcard/Android/data/" + MalodyPackage + "/files/rgvm-" + id + record.Extension;
        var target = "/sdcard/Android/data/" + MalodyPackage + "/files/" + (record.Extension == ".msp" ? "skin" : "chart") + "/rgvm-" + id;
        async Task Save(string stage)
        {
            record.RequestId = request.RequestId;
            record.JobId = DebugOperation.Current.Value?.JobId;
            record.Stage = stage; record.UpdatedAt = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(statePath + ".partial", DebugJson.Write(record), CancellationToken.None);
            File.Move(statePath + ".partial", statePath, overwrite: true);
            var progress = new { importId = id, record.RequestId, record.JobId, stage, directory, remote, target, record.Session, pid = record.App?.Pid,
                record.TransferVerified, record.TransferCount, record.TriggerCount, record.Imported, observedAt = record.UpdatedAt };
            await File.AppendAllTextAsync(Path.Combine(directory, "stages.ndjson"), DebugJson.Write(progress) + "\n", CancellationToken.None);
            Progress.Value?.Invoke(progress);
        }
        try
        {
            record.Error = null; record.FailedStage = null;
            record.Session = Transport.Session;
            await Save(resume ? "resuming" : "prepared");
            if (!record.TransferVerified)
            {
                if (resume) throw new DebugException("transfer_not_verified", "此前传输未完成；请以原始源包开始新导入，不能将残留文件当成可重触发包。");
                await Save("transferring");
                await FilesAsync(DebugRequest.Create("files.push", new { scope = "external", package = MalodyPackage,
                    remote = "files/rgvm-" + id + record.Extension, local = Path.Combine(directory, "source" + record.Extension) }), ct);
                record.TransferCount++;
                var transferredHash = (await ShellAsync("sha256sum " + Q(remote), false, ct)).Split(' ')[0];
                if (!transferredHash.Equals(record.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new DebugException("transfer_mismatch", "上传包散列不一致。");
                record.TransferVerified = true;
                await Save("transferred");
            }
            // Successful extraction can outlive a cancelled caller or a restarted broker.
            record.Imported = await VerifyImportOnceAsync(record, target, directory, ct);
            if (record.Imported.DifferentFiles.Length > 0 || record.Imported.ExtraFiles.Length > 0)
                throw new DebugException("import_content_mismatch", "现有解包资源或签名存在差异，拒绝以重触发覆盖异常；见逐文件核验记录。");
            if (!record.Imported.Verified)
            {
                await Save("waiting_for_app");
                if (request.Command == "malody.reload") await _controller.ForceStopPackageAsync(MalodyPackage, ct);
                record.App = await ObserveApplicationAsync(MalodyPackage, directory, ct);
                // A process restored in the background is not an interactive foreground Activity.
                if (!record.App.ActivityReady) await LaunchProcessAsync(MalodyPackage, directory, ct);
                using var readiness = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readiness.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    do
                    {
                        record.App = await ObserveApplicationAsync(MalodyPackage, directory, readiness.Token);
                        if (record.App.ActivityReady) break;
                        await Task.Delay(500, readiness.Token);
                    } while (true);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new DebugException("app_not_ready", "45秒内未观察到前台 resumed Activity；已传文件保留，可使用importId续作。"); }
                await Save("activity_ready");
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (record.Imported.TargetExists)
                    {
                        await Save("waiting_for_unpack");
                        var existingUnpack = Stopwatch.StartNew();
                        do
                        {
                            record.Imported = await VerifyImportOnceAsync(record, target, directory, ct);
                            if (record.Imported.Verified) break;
                            if (record.Imported.DifferentFiles.Length > 0 || record.Imported.ExtraFiles.Length > 0)
                                throw new DebugException("import_content_mismatch", "已存在的解包资源有真实差异；不再次触发或覆盖。");
                            await Task.Delay(1000, ct);
                        } while (existingUnpack.Elapsed < TimeSpan.FromSeconds(30));
                        if (record.Imported.Verified) break;
                        throw new DebugException("import_incomplete", "App已建立解包目录但内容仍不完整；保留原目录和importId，未重复触发。");
                    }
                    var remoteHash = (await ShellAsync("if test -f " + Q(remote) + "; then sha256sum " + Q(remote) + "; fi", false, ct)).Split(' ')[0].Trim();
                    if (remoteHash.Length == 0 && record.TriggerCount > 0)
                    {
                        // Malody may consume its source before it finishes extracting. Missing
                        // source after a trigger is not permission to upload a second copy.
                        await Save("waiting_for_unpack");
                        var consumed = Stopwatch.StartNew();
                        do
                        {
                            record.Imported = await VerifyImportOnceAsync(record, target, directory, ct);
                            if (record.Imported.Verified) break;
                            await Task.Delay(1000, ct);
                        } while (consumed.Elapsed < TimeSpan.FromSeconds(30));
                        if (record.Imported.Verified) break;
                        throw new DebugException("import_not_observed", "App已消耗导入包，但没有核验到完整解包；可能被App判为重复内容，不能报告成功或自动重复上传。");
                    }
                    if (!remoteHash.Equals(record.SourceSha256, StringComparison.OrdinalIgnoreCase))
                        throw new DebugException("import_source_unavailable", "已传包不再存在或散列变化，禁止自动重复上传；检查解包结果及记录。");
                    await Save("triggering_import");
                    var intent = await AdbAsync(["shell", "am", "start", "-W", "-n", MalodyPackage + "/.MainActivity",
                        "-a", "android.intent.action.VIEW", "-d", remote, "-t", "application/octet-stream"], ct);
                    record.TriggerCount++;
                    await File.WriteAllTextAsync(Path.Combine(directory, $"intent-{record.TriggerCount}.txt"), intent, ct);
                    if (Regex.IsMatch(intent, @"(?im)^\s*(Error|Exception)|Status:\s*(?!ok\b)\w+"))
                        throw new DebugException("import_trigger_failed", "Activity Manager未确认导入Intent成功，见intent依据。");
                    await Save("import_triggered");
                    await Task.Delay(1000, ct);
                    var clock = Stopwatch.StartNew();
                    do
                    {
                        record.Imported = await VerifyImportOnceAsync(record, target, directory, ct);
                        if (record.Imported.Verified) break;
                        await Save(record.Imported.MissingFiles.Length == record.Expected.Count ? "waiting_for_unpack" : "verifying_content");
                        await Task.Delay(1000, ct);
                    } while (clock.Elapsed < TimeSpan.FromSeconds(12));
                    if (record.Imported.Verified) break;
                    // Never retrigger into a partially extracted directory, or hide real mismatches.
                    if (record.Imported.DifferentFiles.Length > 0 || record.Imported.ExtraFiles.Length > 0)
                        throw new DebugException("import_content_mismatch", "解包内容不完整或不同；资源/脚本/签名差异不能作为元数据忽略。");
                    record.App = await ObserveApplicationAsync(MalodyPackage, directory, ct);
                    if (!record.App.ActivityReady) throw new DebugException("app_not_ready", "等待解包时App不再就绪；保留已传文件及恢复编号。");
                }
            }
            if (!record.Imported.Verified) throw new DebugException("import_not_observed", "同一已传文件触发后仍未观察到解包；未报告导入成功，可凭importId续作。");
            record.App = await ObserveApplicationAsync(MalodyPackage, directory, ct);
            await Save("content_verified");
            await Save("waiting_for_activation");
            return new { directory, importId = id, record.SourceSha256, remote, target, record.Session, record.TransferVerified,
                record.TransferCount, record.TriggerCount, imported = record.Imported, importConfirmed = true,
                status = "imported_activation_requires_app_confirmation", stage = record.Stage,
                activationVerified = false, runningVerified = false, app = record.App,
                instructions = "已核验解包内容；应用内启用及实际运行仍未验证。再次查询或续作使用同一importId。" };
        }
        catch (Exception error)
        {
            record.FailedStage = record.Stage;
            var detail = DebugReply.Failure(error).Error!;
            record.Error = detail with { Stage = record.FailedStage, EvidencePath = statePath };
            await Save(error is OperationCanceledException ? "cancelled" : "failed");
            throw new DebugException(detail.Code, detail.Message, record.FailedStage, statePath, error);
        }
    }

    private async Task<ImportVerification> VerifyImportOnceAsync(ImportRecord record, string target, string directory, CancellationToken ct)
    {
        ImportHashEnumeration? previous = null, stable = null;
        for (var scan = 0; scan < 4; scan++)
        {
            var raw = await ShellAsync("if test -d " + Q(target) + "; then echo RGVM_DIRECTORY_PRESENT; find " + Q(target) + " -type f -exec sha256sum {} +; fi", false, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "actual-sha256.txt"), raw, ct);
            var current = ImportContentVerifier.ParseHashes(raw, target);
            await File.AppendAllTextAsync(Path.Combine(directory, "enumeration.ndjson"), DebugJson.Write(new { at = DateTimeOffset.UtcNow,
                current.TargetExists, files = current.Files.Count, current.DuplicateRows, current.ConflictingPaths }) + "\n", ct);
            if (!current.TargetExists || current.Files.Count == 0) { stable = current; break; }
            if (current.ConflictingPaths.Length == 0 && previous is not null && previous.ConflictingPaths.Length == 0 &&
                current.Files.Count == previous.Files.Count && current.Files.All(pair => previous.Files.GetValueOrDefault(pair.Key) == pair.Value))
            { stable = current; break; }
            previous = current; await Task.Delay(250, ct);
        }
        if (stable is null) throw new DebugException("import_snapshot_unstable", "解包目录仍在变化，尚未取得连续一致的散列清单；保留原importId，不能将不稳定采样报成通过。");
        var actual = stable.Files;
        var knownMetadata = false;
        if (record.SourceMetadata is not null && actual.TryGetValue("info.json", out var hash) && hash != record.Expected.GetValueOrDefault("info.json"))
        {
            var json = await ShellAsync("test $(stat -c %s " + Q(target + "/info.json") + ") -le 65536 && cat " + Q(target + "/info.json"), false, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "actual-info.json"), json, ct);
            knownMetadata = ImportContentVerifier.IsKnownMetadataRewrite(record.SourceMetadata, json);
        }
        var result = ImportContentVerifier.Compare(target, record.Expected, actual, knownMetadata) with { TargetExists = stable.TargetExists };
        await File.WriteAllTextAsync(Path.Combine(directory, "verification.json"), DebugJson.Write(result), ct);
        return result;
    }
}
