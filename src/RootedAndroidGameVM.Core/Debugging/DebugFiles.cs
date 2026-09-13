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
        AndroidPackageName.Parse(package);
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
        var package = request.Text("package", MalodyPackage); var scope = request.Text("scope", "external");
        var relative = request.Text("remote"); var remote = RemotePath(scope, package, relative); var root = RemotePath(scope, package, "");
        var local = request.Text("local"); var rootAccess = scope == "private";
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
        // An explicit private file write is permitted, but active encrypted resources must first be unloaded.
        if (scope == "private" && !string.IsNullOrWhiteSpace(await ShellAsync("pidof " + Q(package) + " || true", false, ct)))
            throw new DebugException("app_running", "私有数据写入前请停止应用，避免缓存和运行中的包损坏。");
        var parent = remote[..remote.LastIndexOf('/')];
        var currentParent = root;
        foreach (var segment in relative.Split('/').SkipLast(1))
        {
            var child = "\"$dest\"/" + Q(segment);
            await ShellAsync("set -e; " + Containment(root, currentParent) + "test -d \"$dest\"; if ! test -e " + child + "; then mkdir " + child + "; " +
                (rootAccess ? "chown $(stat -c %u:%g " + Q(root) + ") " + child + "; chmod 700 " + child + "; restorecon " + child + "; " : "") + "fi", rootAccess, ct);
            currentParent += "/" + segment;
        }
        await ShellAsync(Containment(root, parent) + "test -d \"$dest\"", rootAccess, ct);
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
                "chmod $(stat -c %a " + Q(remote) + ") " + Q(stage) + "; " + (rootAccess ? "chown $(stat -c %u:%g " + Q(remote) + ") " + Q(stage) + "; " : "") +
                "else chmod 600 " + Q(stage) + "; " + (rootAccess ? "chown $(stat -c %u:%g " + Q(root) + ") " + Q(stage) + "; " : "") + "fi; " +
                "mv -f " + Q(stage) + " " + Q(remote) + "; " + (rootAccess ? "restorecon " + Q(remote) + "; " : "") + "sync";
            var committed = await ShellAsync(script + "; if test -f " + Q(backupRemote) + "; then echo backup-created; fi", rootAccess, ct);
            return new { remote, backup = committed.Contains("backup-created", StringComparison.Ordinal) ? backupRemote : null, sha256 = hashExpected, atomic = true };
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await ShellAsync("rm -f " + Q(transit) + " " + Q(stage), true, cleanup.Token); }
            catch (Exception e) { var dir = NewRecord("cleanup-required"); await File.WriteAllTextAsync(Path.Combine(dir, "cleanup.json"), DebugJson.Write(new { paths = new[] { transit, stage }, error = e.GetType().Name })); }
        }
    }
}
