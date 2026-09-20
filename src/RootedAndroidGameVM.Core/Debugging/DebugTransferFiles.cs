using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    private static async Task<string> PrefixHashAsync(string path, long count, CancellationToken ct)
    {
        StoragePathPolicy.RejectReparsePoints(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (count > stream.Length || count < 0) throw new DebugException("source_changed", "前缀长度超过源文件。", "verifying_prefix");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536];
        while (count > 0) { var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), ct); if (read == 0) throw new EndOfStreamException(); hash.AppendData(buffer, 0, read); count -= read; }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static string TransferScratch(string target, string plan, int index, string kind) => Path.Combine(Path.GetDirectoryName(target)!, ".rgvm-" + kind + "-" + plan + "-" + index);
    private static void MoveLocal(string source, string target)
    { if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target); }
    private static async Task<string?> CommitLocalTransferAsync(string target, string? temporary, string backup, TransferPlanEntry item,
        TransferFingerprint? expected, bool recovering, CancellationToken ct)
    {
        StoragePathPolicy.RejectReparsePoints(target); StoragePathPolicy.RejectReparsePoints(backup);
        var current = await FileTransferPolicy.LocalFingerprintAsync(target, ct);
        if (recovering && SameContent(current, item.Source))
        {
            var saved = await FileTransferPolicy.LocalFingerprintAsync(backup, ct);
            ValidateRecoveredBackup(saved, item.Source.Kind == "directory" && expected?.Kind == "directory" ? null : expected);
            return saved is null ? null : backup;
        }
        if (Directory.Exists(target) && item.Source.Kind == "directory" && expected?.Kind == "directory") return null;
        var priorBackup = await FileTransferPolicy.LocalFingerprintAsync(backup, ct);
        if (priorBackup is not null)
        {
            if (!recovering || current is not null || !SameContent(priorBackup, expected)) throw new DebugException("recovery_required", "覆盖备份与目标状态不一致，未继续覆盖。", "committing_file", backup);
        }
        else
        {
            if (!SamePlannedTarget(current, expected, false)) throw new DebugException("target_changed", "目标在提交前发生变化。", "committing_file", target);
            if (current is not null) MoveLocal(target, backup);
        }
        try
        {
            if (item.Source.Kind == "directory") { Directory.CreateDirectory(target); ColdCheckpoint.Restrict(target); }
            else { StoragePathPolicy.RejectReparsePoints(temporary!); File.Move(temporary!, target); ColdCheckpoint.RestrictFile(target); }
        }
        catch
        {
            if (!File.Exists(target) && !Directory.Exists(target) && (File.Exists(backup) || Directory.Exists(backup))) MoveLocal(backup, target);
            throw;
        }
        return File.Exists(backup) || Directory.Exists(backup) ? backup : null;
    }
    private async Task<TransferItemState> DownloadTransferEntryAsync(TransferPlan plan, TransferPlanEntry item, TransferSelection selection, ResolvedFileRoot root,
        TransferFingerprint source, TransferItemState state, TransferExecutionStore journal, bool resume, CancellationToken ct)
    {
        var archive = plan.Format == "tar";
        var cache = Path.Combine(plan.ArtifactDirectory, "archive-content");
        var target = archive ? Path.Combine(cache, item.Index + ".data") : FileTransferPolicy.LocalTarget(plan.DestinationPath, state.TargetRelativePath);
        if (archive) ColdCheckpoint.Restrict(cache);
        var expected = state.TargetRelativePath == item.TargetRelativePath ? item.Target : null;
        var backup = TransferScratch(target, plan.PlanId, item.Index, "backup");
        var temporary = archive ? target : TransferScratch(target, plan.PlanId, item.Index, "stage");
        if (item.Source.Kind is "directory" or "symlink")
        {
            if (archive) return state with { Status = "staged" };
            state = state with { Status = "committing", BackupPath = backup }; await journal.SaveItemAsync(state, ct);
            var saved = await CommitLocalTransferAsync(target, null, backup, item, expected, resume, ct);
            return state with { Status = "completed", BackupPath = saved, TemporaryPath = null };
        }
        if (state.Status == "committing" && !archive && SameContent(await FileTransferPolicy.LocalFingerprintAsync(target, ct), item.Source))
        {
            var saved = await CommitLocalTransferAsync(target, null, backup, item, expected, true, ct);
            return state with { Status = "completed", Sha256 = item.Source.Sha256, Offset = item.Source.Bytes, TemporaryPath = null, BackupPath = saved };
        }
        StoragePathPolicy.RejectReparsePoints(temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(temporary)) { using (File.Create(temporary)) { } ColdCheckpoint.RestrictFile(temporary); }
        var offset = new FileInfo(temporary).Length;
        var relative = FileTransferPolicy.JoinRemote(selection.SourcePath, item.RelativePath);
        if (offset > item.Source.Bytes) throw new DebugException("staging_changed", "暂存文件长度超过计划。", "verifying_prefix", temporary);
        if (offset > 0)
        {
            var prefix = await FileBridgeAsync(new { op = "transfer-hash-prefix", root = root.Path, relativePath = relative, version = source.Version, length = offset }, root.Identity.Session, ct);
            if (await PrefixHashAsync(temporary, offset, ct) != prefix.GetProperty("sha256").GetString())
                throw new DebugException("staging_mismatch", "暂存前缀与源不一致，未继续写入目标。", "verifying_prefix", temporary);
        }
        state = state with { Status = "staging", Offset = offset, TemporaryPath = temporary, BackupPath = archive ? null : backup };
        await journal.SaveItemAsync(state, ct);
        var wire = Path.Combine(plan.ArtifactDirectory, "download-" + item.Index + ".chunk");
        try
        {
            while (offset < item.Source.Bytes)
            {
                var count = (int)Math.Min(TransferChunkBytes, item.Source.Bytes - offset); RequireCatalogSession(root.Identity.Session);
                LocalTransferSpace.Require(Path.GetDirectoryName(temporary)!, count);
                LocalTransferSpace.Require(plan.ArtifactDirectory, count);
                var helper = await EnsureCatalogHelperAsync(root.Identity.Session, ct);
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(DebugJson.Write(new { root = root.Path, relativePath = relative, version = source.Version, offset, length = count })));
                if (File.Exists(wire)) File.Delete(wire);
                var script = await OwnedGuestScriptAsync("CLASSPATH=" + Q(helper) + " app_process / dev.rgvm.catalog.Main read " + Q(encoded), ct);
                var copied = await BinaryProcess.RunToFileAsync(AndroidCommandFactory.RootExecOut(Layout, Options, script), wire, count, ct);
                RequireCatalogSession(root.Identity.Session);
                if (copied != count) throw new DebugException("source_changed", "读取块长度不符合计划。", "reading_chunk");
                await using (var output = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None, 65536, true))
                await using (var input = File.OpenRead(wire))
                { if (output.Length != offset) throw new IOException("暂存长度改变。"); output.Position = offset; await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true); }
                offset += count; state = state with { Offset = offset }; await journal.SaveItemAsync(state, ct);
                Progress.Value?.Invoke(new { stage = "downloading", plan.PlanId, item.Index, bytes = offset, totalBytes = item.Source.Bytes, directory = plan.ArtifactDirectory });
            }
        }
        finally { if (File.Exists(wire)) File.Delete(wire); }
        if (await PrefixHashAsync(temporary, item.Source.Bytes, ct) != item.Source.Sha256) throw new DebugException("checksum_mismatch", "下载散列与计划不同，未覆盖目标。", "verifying_download", temporary);
        if (archive) return state with { Status = "staged", Sha256 = item.Source.Sha256 };
        state = state with { Status = "committing", Sha256 = item.Source.Sha256 }; await journal.SaveItemAsync(state, ct);
        var savedBackup = await CommitLocalTransferAsync(target, temporary, backup, item, expected, resume, ct);
        if (item.Source.ModifiedUnixMs is { } modified) File.SetLastWriteTimeUtc(target, DateTimeOffset.FromUnixTimeMilliseconds(modified).UtcDateTime);
        return state with { Status = "completed", BackupPath = savedBackup, TemporaryPath = null };
    }
    private async Task<TransferItemState> UploadTransferEntryAsync(TransferPlan plan, TransferPlanEntry item, TransferSelection? selection, ResolvedFileRoot root,
        TransferFingerprint source, TransferItemState state, TransferExecutionStore journal, bool resume, CancellationToken ct)
    {
        var relative = FileTransferPolicy.JoinRemote(plan.DestinationPath, state.TargetRelativePath);
        FileTransferPolicy.ValidateAnchoredTarget(plan, relative);
        var expected = state.TargetRelativePath == item.TargetRelativePath ? item.Target : null;
        var privateData = root.Identity.Kind is "private" or "device-private";
        var externalData = FileTransferPolicy.RequiresApplicationOwner(plan, relative);
        if (externalData && root.ApplicationUid is null) throw new DebugException("metadata_unavailable", "未取得所选应用的UID，未写入应用外部目录。", "verifying_permissions");
        var scope = privateData ? "private" : root.Identity.Kind == "shared" ? "shared" : "external";
        var owner = await FileBridgeAsync(new { op = "stat", root = root.Path, relativePath = "" }, root.Identity.Session, ct);
        object Commit(string op) => new
        {
            op,
            root = root.Path,
            relativePath = relative,
            planId = plan.PlanId,
            index = item.Index,
            expectedTarget = expected,
            recover = resume,
            privateData,
            externalData,
            applicationUid = root.ApplicationUid,
            uid = owner.GetProperty("uid").GetInt32(),
            gid = owner.GetProperty("gid").GetInt32(),
            fileMode = FileAccessPolicy.FileMode(scope, expected?.Mode),
            directoryMode = plan.DestinationAnchor is not null && FileTransferPolicy.ApplicationStoragePrefix(root.Identity).StartsWith(relative + "/", StringComparison.Ordinal)
                ? "755" : FileAccessPolicy.DirectoryMode(scope),
            length = item.Source.Bytes,
            sha256 = item.Source.Sha256,
            modifiedUnixMs = item.Source.ModifiedUnixMs
        };
        if (item.Source.Kind == "directory")
        {
            if (resume && state.Status == "committing")
            {
                var recovered = await FileBridgeAsync(new { op = "transfer-state", root = root.Path, relativePath = relative, planId = plan.PlanId, index = item.Index }, root.Identity.Session, ct);
                // A lost mkdir receipt can be completed by merging the directory. Child
                // targets still retain their own planned identity checks; never replace it.
                if (recovered.TryGetProperty("target", out var existingDirectory) && existingDirectory.GetProperty("kind").GetString() == "directory")
                {
                    var directoryTarget = existingDirectory.Deserialize<RemoteFileEntry>(DebugJson.Options)!;
                    var saved = await ReconcileRemoteBackupAsync(plan, item, state, root, recovered, expected?.Kind == "directory" ? null : expected, ct);
                    return state with
                    {
                        Status = "completed",
                        BackupPath = saved,
                        TemporaryPath = null,
                        Permissions = directoryTarget.Uid + ":" + directoryTarget.Gid + ":" + directoryTarget.Mode
                    };
                }
            }
            state = state with { Status = "committing" }; await journal.SaveItemAsync(state, ct);
            var directory = await FileBridgeAsync(Commit("transfer-mkdir"), root.Identity.Session, ct);
            return state with { Status = "completed", BackupPath = CatalogText(directory, "backupPath") };
        }
        var observed = await FileBridgeAsync(new { op = "transfer-state", root = root.Path, relativePath = relative, planId = plan.PlanId, index = item.Index }, root.Identity.Session, ct);
        if (state.Status == "committing" && observed.TryGetProperty("target", out var committed) && SameContent(Fingerprint(committed.Deserialize<RemoteFileEntry>(DebugJson.Options)!), item.Source))
        {
            var actual = committed.Deserialize<RemoteFileEntry>(DebugJson.Options)!;
            var saved = await ReconcileRemoteBackupAsync(plan, item, state, root, observed, expected, ct);
            return state with
            {
                Status = "completed",
                Offset = item.Source.Bytes,
                Sha256 = actual.Sha256,
                TemporaryPath = null,
                BackupPath = saved,
                Permissions = actual.Uid + ":" + actual.Gid + ":" + actual.Mode
            };
        }
        var sourcePath = item.RelativePath.Length == 0 ? selection!.SourcePath : Path.Combine(selection!.SourcePath, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var offset = observed.GetProperty("bytes").GetInt64();
        if (offset > item.Source.Bytes || offset > 0 && await PrefixHashAsync(sourcePath, offset, ct) != CatalogText(observed, "sha256"))
            throw new DebugException("staging_mismatch", "安卓暂存前缀与源不符，未覆盖目标。", "verifying_prefix");
        state = state with { Status = "staging", Offset = offset, TemporaryPath = CatalogText(observed, "stagePath"), BackupPath = CatalogText(observed, "backupPath") };
        await journal.SaveItemAsync(state, ct);
        var wireRoot = "/data/local/tmp/rgvm-transfer-wire/" + plan.PlanId;
        var wire = wireRoot + "/" + item.Index + ".chunk"; var local = Path.Combine(plan.ArtifactDirectory, "upload-" + item.Index + ".chunk");
        await ShellAsync("set -e; test ! -L /data/local/tmp/rgvm-transfer-wire; mkdir -p /data/local/tmp/rgvm-transfer-wire; chmod 711 /data/local/tmp/rgvm-transfer-wire; test ! -L " + Q(wireRoot) +
            "; mkdir -p " + Q(wireRoot) + "; chown 2000:2000 " + Q(wireRoot) + "; chmod 700 " + Q(wireRoot), true, ct);
        try
        {
            var empty = item.Source.Bytes == 0 && CatalogText(observed, "sha256") is null;
            while (offset < item.Source.Bytes || empty)
            {
                empty = false; RequireCatalogSession(root.Identity.Session);
                var current = new FileInfo(sourcePath);
                if (current.CreationTimeUtc.Ticks + ":" + current.LastWriteTimeUtc.Ticks + ":" + current.Length != source.Version) throw new DebugException("source_changed", "上传源已改变。", "reading_chunk");
                var count = (int)Math.Min(TransferChunkBytes, item.Source.Bytes - offset);
                LocalTransferSpace.Require(plan.ArtifactDirectory, count);
                LocalTransferSpace.Require(Paths.AvdHome, checked(count * 2L + 65536));
                await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                await using (var output = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    input.Position = offset; var remaining = count; var buffer = new byte[65536];
                    while (remaining > 0) { var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), ct); if (read == 0) throw new EndOfStreamException(); await output.WriteAsync(buffer.AsMemory(0, read), ct); remaining -= read; }
                }
                ColdCheckpoint.RestrictFile(local); var hash = await PrefixHashAsync(local, count, ct);
                await AdbAsync(["push", local, wire], ct); RequireCatalogSession(root.Identity.Session);
                var appended = await FileBridgeAsync(new { op = "transfer-append", root = root.Path, relativePath = relative, planId = plan.PlanId, index = item.Index, wire, offset, length = count, sha256 = hash }, root.Identity.Session, ct);
                offset += count;
                if (appended.GetProperty("bytes").GetInt64() != offset) throw new DebugException("staging_changed", "暂存长度不符合提交块。", "uploading");
                state = state with { Offset = offset }; await journal.SaveItemAsync(state, ct);
                Progress.Value?.Invoke(new { stage = "uploading", plan.PlanId, item.Index, bytes = offset, totalBytes = item.Source.Bytes, directory = plan.ArtifactDirectory });
            }
        }
        finally
        {
            if (File.Exists(local)) File.Delete(local);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { RequireCatalogSession(root.Identity.Session); await ShellAsync("rm -f " + Q(wire), true, cleanup.Token); }
            catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(plan.ArtifactDirectory, "cleanup-required-" + item.Index + ".json"), DebugJson.Write(new { wire, session = root.Identity.Session, error = error.Message }), CancellationToken.None); }
        }
        state = state with { Status = "committing" }; await journal.SaveItemAsync(state, ct);
        var result = await FileBridgeAsync(Commit("transfer-commit"), root.Identity.Session, ct);
        var target = result.GetProperty("target").Deserialize<RemoteFileEntry>(DebugJson.Options)!;
        if (target.Sha256 != item.Source.Sha256 || target.Bytes != item.Source.Bytes) throw new DebugException("checksum_mismatch", "提交后的安卓文件核验失败。", "verifying_target");
        return state with
        {
            Status = "completed",
            TemporaryPath = null,
            BackupPath = CatalogText(result, "backupPath"),
            Sha256 = target.Sha256,
            Permissions = target.Uid + ":" + target.Gid + ":" + target.Mode
        };
    }
    private async Task<string?> ReconcileRemoteBackupAsync(TransferPlan plan, TransferPlanEntry item, TransferItemState state,
        ResolvedFileRoot root, JsonElement observed, TransferFingerprint? expected, CancellationToken ct)
    {
        var exists = observed.GetProperty("backupExists").GetBoolean();
        if (!exists) { ValidateRecoveredBackup(null, expected); return null; }
        if (expected is null) { ValidateRecoveredBackup(new("unknown", 0, "", null), null); }
        var relative = FileTransferPolicy.JoinRemote(plan.DestinationPath, state.TargetRelativePath);
        var slash = relative.LastIndexOf('/');
        var backupRelative = (slash < 0 ? "" : relative[..(slash + 1)]) + ".rgvm-backup-" + plan.PlanId + "-" + item.Index;
        var backup = Fingerprint((await FileBridgeAsync(new { op = "stat", root = root.Path, relativePath = backupRelative, hash = expected!.Kind == "file" }, root.Identity.Session, ct))
            .Deserialize<RemoteFileEntry>(DebugJson.Options)!);
        ValidateRecoveredBackup(backup, expected);
        return CatalogText(observed, "backupPath");
    }
    private static void ValidateRecoveredBackup(TransferFingerprint? backup, TransferFingerprint? expected)
    {
        if (backup is null)
        {
            if (expected is not null) throw new DebugException("backup_missing", "原计划覆盖前的备份已不在，未将回执标为完整恢复。", "reconciling_commit");
            return;
        }
        if (expected is null) throw new DebugException("backup_unexpected", "新建或合并条目出现未知备份，未猜测其归属。", "reconciling_commit");
        if (!SameContent(backup, expected) || backup.Uid != expected.Uid || backup.Gid != expected.Gid || backup.Mode != expected.Mode ||
            backup.Kind == "directory" && (backup.Bytes != expected.Bytes || backup.ModifiedUnixMs != expected.ModifiedUnixMs))
            throw new DebugException("backup_changed", "覆盖备份与原计划的内容或元数据不同，未重新覆盖。", "reconciling_commit");
    }
    private async Task<TransferExecutionHeader> CommitTransferArchiveAsync(TransferPlan plan, TransferExecutionHeader header,
        Dictionary<int, TransferItemState> states, TransferExecutionStore journal, CancellationToken ct)
    {
        var target = Path.Combine(plan.DestinationPath, "app-data-" + plan.PlanId + ".tar");
        StoragePathPolicy.RejectReparsePoints(target);
        if (File.Exists(target))
        {
            if (header.ArchiveSha256 is null || await PrefixHashAsync(target, new FileInfo(target).Length, ct) != header.ArchiveSha256)
                throw new DebugException("target_changed", "归档目标已存在且不能确认归属。", "committing_archive");
            return header with { ArchivePath = target };
        }
        var temporary = target + ".partial"; StoragePathPolicy.RejectReparsePoints(temporary);
        using (File.Create(temporary)) { }
        ColdCheckpoint.RestrictFile(temporary);
        LocalTransferSpace.Require(plan.DestinationPath, checked(plan.Entries.Where(item => item.Source.Kind == "file").Sum(item => TransferSpacePolicy.RoundUp(item.Source.Bytes, 512)) + plan.Entries.Length * 16384L + 1024));
        await using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None, 65536, true))
        {
            await using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
            foreach (var item in plan.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var type = item.Source.Kind switch { "directory" => TarEntryType.Directory, "symlink" => TarEntryType.SymbolicLink, _ => TarEntryType.RegularFile };
                var entry = new PaxTarEntry(type, item.TargetRelativePath)
                {
                    Uid = item.Source.Uid ?? 0,
                    Gid = item.Source.Gid ?? 0,
                    Mode = (UnixFileMode)Convert.ToInt32(item.Source.Mode ?? (type == TarEntryType.Directory ? "700" : "600"), 8)
                };
                if (item.Source.ModifiedUnixMs is { } modified) entry.ModificationTime = DateTimeOffset.FromUnixTimeMilliseconds(modified);
                if (type == TarEntryType.SymbolicLink) entry.LinkName = item.Source.LinkTarget!;
                if (type == TarEntryType.RegularFile)
                {
                    await using var content = File.OpenRead(states[item.Index].TemporaryPath!); entry.DataStream = content;
                    await writer.WriteEntryAsync(entry, ct);
                }
                else await writer.WriteEntryAsync(entry, ct);
            }
        }
        var sha = await PrefixHashAsync(temporary, new FileInfo(temporary).Length, ct);
        header = header with { Status = "committing_archive", ArchivePath = target, ArchiveSha256 = sha, UpdatedAt = DateTimeOffset.UtcNow };
        await journal.SaveHeaderAsync(header, ct); File.Move(temporary, target);
        return header;
    }
}
