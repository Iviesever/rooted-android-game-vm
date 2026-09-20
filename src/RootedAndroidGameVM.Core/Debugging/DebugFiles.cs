using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed partial class AndroidDebugService
{
    public static string RemotePath(string scope, string package, string relative)
    {
        if (scope != "shared" || !string.IsNullOrEmpty(package)) AndroidPackageName.Parse(package);
        if (relative.Contains('\\') || relative.Contains('\0') || relative.Contains('\n') || relative.Contains('\r') ||
            relative.StartsWith('/') || relative.Split('/').Any(p => p is ".." or ".")) throw new ArgumentException("安卓路径必须是范围内的相对路径。");
        var root = scope switch
        {
            "private" => "/data/data/" + package,
            "external" => "/sdcard/Android/data/" + package,
            "shared" => "/sdcard/Download",
            _ => throw new ArgumentException("scope 必须是 private、external 或 shared。")
        };
        return root + (relative.Length == 0 ? "" : "/" + relative);
    }
    private static string Containment(string root, string target) =>
        "base=$(realpath " + Q(root) + ") || exit 41; dest=$(realpath " + Q(target) + ") || exit 42; " +
        "case \"$dest\" in \"$base\"|\"$base\"/*) ;; *) echo 'symlink escape' >&2; exit 43;; esac; ";
    public async Task<object> FilesAsync(DebugRequest request, CancellationToken ct)
    {
        var scope = request.Text("scope", "external");
        var package = scope == "shared" ? request.Text("package") : RequirePackage(request);
        var relative = request.Text("remote"); var remote = RemotePath(scope, package, relative); var root = RemotePath(scope, package, "");
        var local = request.Text("local"); var rootAccess = scope == "private";
        if (request.Command == "files.export") return await ExportFolderAsync(package, root, remote, local, rootAccess, ct);
        if (request.Command == "files.list")
        {
            var output = await ShellAsync(Containment(root, remote) + "find \"$dest\" -mindepth 1 -maxdepth 1 -print0", rootAccess, ct);
            var entries = new List<object>();
            foreach (var name in output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Take(4096))
            {
                var info = await ShellAsync("stat -c '%F|%s|%u|%g|%a' " + Q(name), rootAccess, ct);
                entries.Add(new { name = name.Split('/')[^1], details = info.Trim() });
            }
            return new { scope, package, remote = relative, entries };
        }
        if (request.Command is "files.diff" or "files.sync")
        {
            if (!Directory.Exists(local)) throw new DirectoryNotFoundException(local);
            StoragePathPolicy.RejectReparsePoints(local);
            var results = new List<object>();
            foreach (var file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).Take(4097))
            {
                if (results.Count == 4096) throw new IOException("同步文件数超过 4096。");
                StoragePathPolicy.RejectReparsePoints(file);
                var rel = (relative.TrimEnd('/') + "/" + Path.GetRelativePath(local, file).Replace('\\', '/')).TrimStart('/');
                var target = RemotePath(scope, package, rel);
                var hash = (await ColdCheckpoint.DigestAsync(local, file, ct)).Sha256;
                string? current = null;
                try { current = (await ShellAsync(Containment(root, target) + "sha256sum \"$dest\"", rootAccess, ct)).Split(' ')[0].Trim(); }
                catch (DebugException e) when (e.Code == "tool_failed") { }
                var equal = string.Equals(hash, current, StringComparison.OrdinalIgnoreCase);
                object? write = null;
                if (!equal && request.Command == "files.sync") write = await FilesAsync(DebugRequest.Create("files.push", new { scope, package, remote = rel, local = file }), ct);
                results.Add(new { file = rel, equal, write });
            }
            return new { deleted = false, results };
        }
        if (request.Command == "files.pull")
        {
            if (string.IsNullOrWhiteSpace(local)) local = Path.Combine(NewRecord("download"), Path.GetFileName(remote));
            local = Path.GetFullPath(local); StoragePathPolicy.RejectReparsePoints(local);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            var sourceInfo = (await ShellAsync(Containment(root, remote) + "test -f \"$dest\" || exit 44; stat -c %s \"$dest\"; sha256sum \"$dest\"", rootAccess, ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var expectedSize = long.Parse(sourceInfo[0].Trim()); var expectedHash = sourceInfo[1].Split(' ')[0].Trim();
            if (expectedSize > 512L * 1024 * 1024) throw new IOException("单文件下载限制为 512 MiB。");
            ColdCheckpoint.RequireSpace(local, expectedSize);
            var remoteTemp = "/data/local/tmp/rgvm-download-" + Guid.NewGuid().ToString("N");
            var temp = local + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                if (rootAccess) await ShellAsync("umask 077; cp " + Q(remote) + " " + Q(remoteTemp) + " && chown 2000:2000 " + Q(remoteTemp) + " && chmod 600 " + Q(remoteTemp), true, ct);
                using (File.Create(temp)) { }
                ColdCheckpoint.RestrictFile(temp);
                await AdbAsync(["pull", rootAccess ? remoteTemp : remote, temp], ct);
                var downloaded = await ColdCheckpoint.DigestAsync(Path.GetDirectoryName(temp)!, temp, ct);
                if (downloaded.Length != expectedSize || !downloaded.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("下载长度或散列不一致；未覆盖目标文件。");
                ct.ThrowIfCancellationRequested();
                string? backup = null;
                if (File.Exists(local)) { backup = local + ".backup-" + Guid.NewGuid().ToString("N"); File.Replace(temp, local, backup); }
                else File.Move(temp, local);
                ColdCheckpoint.RestrictFile(local);
                if (backup is not null) ColdCheckpoint.RestrictFile(backup);
                return new { local, backup, sha256 = downloaded.Sha256 };
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
                if (rootAccess)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await ShellAsync("rm -f " + Q(remoteTemp), true, cleanup.Token); }
                    catch { await File.WriteAllTextAsync(Path.Combine(NewRecord("cleanup-required"), "sensitive-temporary-file.txt"), remoteTemp); }
                }
            }
        }
        if (request.Command != "files.push") throw new ArgumentException("未知文件操作。");
        if (relative.Length == 0) throw new ArgumentException("上传目标必须包含文件名。");
        StoragePathPolicy.RejectReparsePoints(local);
        if (!File.Exists(local)) throw new FileNotFoundException("上传文件不存在。", local);
        if (new FileInfo(local).Length > 512L * 1024 * 1024) throw new IOException("单文件上传限制为 512 MiB。");
        // Stop the application before modifying its private data to avoid live cache/database conflicts.
        if (scope == "private" && !string.IsNullOrWhiteSpace(await ShellAsync("pidof " + Q(package) + " || true", false, ct)))
            throw new DebugException("app_running", "私有数据写入前请停止应用，避免缓存和运行中的包损坏。");
        var parent = remote[..remote.LastIndexOf('/')];
        var currentParent = root;
        foreach (var segment in relative.Split('/').SkipLast(1))
        {
            var child = "\"$dest\"/" + Q(segment);
            await ShellAsync("set -e; " + Containment(root, currentParent) + "test -d \"$dest\"; if ! test -e " + child + "; then mkdir " + child + "; " +
                "chmod " + FileAccessPolicy.DirectoryMode(scope) + " " + child + "; " +
                (rootAccess ? "chown $(stat -c %u:%g " + Q(root) + ") " + child + "; restorecon " + child + "; " : "") + "fi", rootAccess, ct);
            currentParent += "/" + segment;
        }
        await ShellAsync(Containment(root, parent) + "test -d \"$dest\"", rootAccess, ct);
        var previousMode = (await ShellAsync(Containment(root, parent) + "test ! -L " + Q(remote) +
            " || exit 43; if test -e " + Q(remote) + "; then test -f " + Q(remote) + " || exit 44; stat -c %a " + Q(remote) + "; fi", rootAccess, ct)).Trim();
        var fileMode = FileAccessPolicy.FileMode(scope, previousMode.Length == 0 ? null : previousMode);
        var hashExpected = (await ColdCheckpoint.DigestAsync(Path.GetDirectoryName(Path.GetFullPath(local))!, Path.GetFullPath(local), ct)).Sha256.ToLowerInvariant();
        var nonce = Guid.NewGuid().ToString("N"); var transit = "/data/local/tmp/rgvm-upload-" + nonce;
        var stage = parent + "/.rgvm-stage-" + nonce; var backupRemote = parent + "/.rgvm-backup-" + nonce;
        try
        {
            await AdbAsync(["push", local, transit], ct);
            var script = "set -e; umask 077; " + Containment(root, parent) +
                "test ! -L " + Q(remote) + "; " +
                "available=$(df -Pk \"$dest\" | tail -1 | awk '{print $4}'); test \"$available\" -ge " + ((new FileInfo(local).Length * 2 / 1024) + 65536) + "; " +
                "cp " + Q(transit) + " " + Q(stage) + "; " +
                "test \"$(sha256sum " + Q(stage) + " | cut -d' ' -f1)\" = " + Q(hashExpected) + "; " +
                "if test -e " + Q(remote) + "; then cp -p " + Q(remote) + " " + Q(backupRemote) + "; " +
                "fi; chmod " + fileMode + " " + Q(stage) + "; " + (rootAccess ? "chown $(stat -c %u:%g " + Q(root) + ") " + Q(stage) + "; " : "") +
                "mv -f " + Q(stage) + " " + Q(remote) + "; " + (rootAccess ? "restorecon " + Q(remote) + "; " : "") + "sync";
            var committed = await ShellAsync(script + "; if test -f " + Q(backupRemote) + "; then echo backup-created; fi", rootAccess, ct);
            var permissions = (await ShellAsync("stat -c '%u:%g:%a' " + Q(remote), rootAccess, ct)).Trim();
            return new { remote, backup = committed.Contains("backup-created", StringComparison.Ordinal) ? backupRemote : null,
                sha256 = hashExpected, atomic = true, scope, permissions, applicationReadVerified = false };
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await ShellAsync("rm -f " + Q(transit) + " " + Q(stage), true, cleanup.Token); }
            catch (Exception e) { var dir = NewRecord("cleanup-required"); await File.WriteAllTextAsync(Path.Combine(dir, "cleanup.json"), DebugJson.Write(new { paths = new[] { transit, stage }, error = e.GetType().Name })); }
        }
    }
    private async Task<object> ExportFolderAsync(string package, string root, string remote, string destination, bool privateData, CancellationToken ct)
    {
        Instance.Require(force: true);
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("请选择导出保存位置。");
        destination = Path.GetFullPath(destination); StoragePathPolicy.RejectReparsePoints(destination);
        Directory.CreateDirectory(destination);
        var id = Guid.NewGuid().ToString("N");
        var output = Path.Combine(destination, (package.Length == 0 ? "shared" : package) + "-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + id[..8]);
        var temporary = "/data/local/tmp/rgvm-export-" + id + ".tar";
        var localArchive = Path.Combine(output, "transfer.tar");
        ColdCheckpoint.Restrict(output);
        var success = false;
        try
        {
            var sizes = (await ShellAsync(Containment(root, remote) + "test -d \"$dest\" || exit 44; find \"$dest\" -type f -exec stat -c %s {} \\;", privateData, ct))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (sizes.Length > 100000) throw new IOException("导出文件数量超限。");
            long apparentBytes = 0;
            foreach (var size in sizes)
            {
                if (!long.TryParse(size, out var bytes) || bytes < 0 || bytes > 8L * 1024 * 1024 * 1024 - apparentBytes)
                    throw new IOException("导出目录的逻辑大小超过 8 GiB，已停止。");
                apparentBytes += bytes;
            }
            var archiveLimit = apparentBytes + sizes.Length * 2048L + 16 * 1024 * 1024;
            ColdCheckpoint.RequireSpace(output, archiveLimit + apparentBytes);
            // Check apparent sizes (sparse files included) and bound the producer even if files grow after scanning.
            await ShellAsync("set -e; set -o pipefail; umask 077; " + Containment(root, remote) +
                "free=$(df -Pk /data/local/tmp | tail -1 | awk '{print $4}'); test \"$free\" -ge " + (archiveLimit / 1024 + 65536) + " || { echo 'No space left' >&2; exit 45; }; " +
                "tar -C \"$dest\" -cf - . | head -c " + (archiveLimit + 1) + " > " + Q(temporary) + "; " +
                "test $(stat -c %s " + Q(temporary) + ") -le " + archiveLimit + " || { echo 'archive grew beyond limit' >&2; exit 46; }; " +
                "chown 2000:2000 " + Q(temporary) + "; chmod 600 " + Q(temporary), true, ct);
            var expected = (await ShellAsync("sha256sum " + Q(temporary), true, ct)).Split(' ')[0];
            await AdbAsync(["pull", temporary, localArchive], ct);
            var received = await ColdCheckpoint.DigestAsync(output, localArchive, ct);
            if (!received.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("导出归档校验失败。");
            ct.ThrowIfCancellationRequested();
            IO.SafeTarExtractor.Extract(localArchive, Path.Combine(output, "files"));
            success = true;
            return new { directory = output, dataDirectory = Path.Combine(output, "files"), includesPrivateData = privateData };
        }
        finally
        {
            var pendingCleanup = new List<string>();
            try { if (File.Exists(localArchive)) File.Delete(localArchive); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { pendingCleanup.Add(localArchive); }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await ShellAsync("rm -f " + Q(temporary), true, cleanup.Token); }
            catch { pendingCleanup.Add(temporary); }
            if (!success) await File.WriteAllTextAsync(Path.Combine(output, "INCOMPLETE.txt"), "导出未完成；此目录不是有效备份。");
            if (pendingCleanup.Count > 0)
            {
                await File.WriteAllTextAsync(Path.Combine(output, "cleanup-required.txt"), "Sensitive temporary archives:\n" + string.Join('\n', pendingCleanup));
                if (success) throw new IOException("内容已导出，但敏感临时归档未能全部清理。请查看 " + Path.Combine(output, "cleanup-required.txt"));
            }
        }
    }
}
