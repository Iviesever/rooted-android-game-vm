using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record TransferSource(string? EntryRef = null, string? RootRef = null, string RelativePath = "", string? LocalPath = null);
public sealed record TransferDestination(string? LocalDirectory = null, string? EntryRef = null, string? RootRef = null, string RelativePath = "");
public sealed record TransferSelection(string Id, string SourcePath, FileRootIdentity? RemoteRoot, string SourceKind, string TargetPrefix, string? Version);
public sealed record TransferFingerprint(string Kind, long Bytes, string Version, string? Sha256, int? Uid = null, int? Gid = null, string? Mode = null, string? LinkTarget = null);
public sealed record TransferPlanEntry(int Index, string SelectionId, string RelativePath, string TargetRelativePath,
    TransferFingerprint Source, TransferFingerprint? Target, string Conflict, string? Issue = null);
public sealed record TransferApplication(string Package, int UserId, string InstallationRevision, bool RunningObserved);
public sealed record TransferPlan(string PlanId, DateTimeOffset CreatedAt, string InstanceId, string Session, string Direction,
    string Format, string Consistency, TransferSelection[] Selections, FileRootIdentity? DestinationRoot, string DestinationPath,
    TransferPlanEntry[] Entries, TransferApplication[] ApplicationsToStop, long TotalBytes, long RequiredBytes, long? AvailableBytes,
    string[] Issues, string ArtifactDirectory, string Status = "planned", bool TransferVerified = false);

public static class FileTransferPolicy
{
    public const int MaxEntries = 100000;
    public static string JoinRemote(string left, string right) => left.Length == 0 ? right : right.Length == 0 ? left : left + "/" + right;
    public static string? WindowsNameIssue(string relative)
    {
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment.Length > 255 || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(character => character < 32 || "<>:\"\\|?*".Contains(character))) return "windows_name_unsupported";
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')) return "windows_name_unsupported";
        }
        return null;
    }
    public static string Conflict(TransferFingerprint source, TransferFingerprint? target)
    {
        if (target is null) return "new";
        if (source.Kind != target.Kind) return "type_conflict";
        if (source.Kind == "directory") return "merge";
        return source.Kind == "file" && source.Bytes == target.Bytes && source.Sha256 is not null &&
            source.Sha256.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase) ? "same" : "different";
    }
    public static string LocalTarget(string directory, string relative)
    {
        FileReferences.Relative(relative);
        if (WindowsNameIssue(relative) is { } issue) throw new DebugException(issue, "该名称不能原样保存到Windows，请选择归档格式。", "planning_transfer");
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!StoragePathPolicy.Contains(root, path)) throw new DebugException("path_escape", "本地目标越界。", "planning_transfer");
        StoragePathPolicy.RejectReparsePoints(path);
        return path;
    }
    public static async Task<TransferFingerprint?> LocalFingerprintAsync(string path, CancellationToken ct)
    {
        StoragePathPolicy.RejectReparsePoints(path);
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return new("directory", 0, directory.CreationTimeUtc.Ticks + ":" + directory.LastWriteTimeUtc.Ticks, null);
        }
        if (!File.Exists(path)) return null;
        var before = new FileInfo(path);
        var bytes = before.Length; var modified = before.LastWriteTimeUtc.Ticks; var created = before.CreationTimeUtc.Ticks;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        var after = new FileInfo(path);
        StoragePathPolicy.RejectReparsePoints(path);
        if (after.Length != bytes || after.LastWriteTimeUtc.Ticks != modified || after.CreationTimeUtc.Ticks != created)
            throw new DebugException("source_changed", "文件在扫描时发生变化。", "planning_transfer", path);
        return new("file", bytes, created + ":" + modified + ":" + bytes, hash);
    }
}

[SupportedOSPlatform("windows")]
public sealed class TransferPlanStore(string productRoot)
{
    public string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("planId无效。");
        var path = Path.Combine(productRoot, "debug-runs", "transfers", id);
        StoragePathPolicy.RejectReparsePoints(path);
        return path;
    }
    public async Task SaveAsync(TransferPlan plan, CancellationToken ct)
    {
        var path = DirectoryFor(plan.PlanId); ColdCheckpoint.Restrict(path);
        await AtomicJsonFile.WriteAsync(Path.Combine(path, "plan.json"), plan, ct);
        ColdCheckpoint.RestrictFile(Path.Combine(path, "plan.json"));
    }
    public async Task<TransferPlan> ReadAsync(string id, CancellationToken ct)
    {
        var path = Path.Combine(DirectoryFor(id), "plan.json");
        StoragePathPolicy.RejectReparsePoints(path);
        if (!File.Exists(path)) throw new DebugException("plan_not_found", "传输计划不存在。", "reading_transfer_plan");
        if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidDataException("传输计划超过容量限制。");
        await using var stream = File.OpenRead(path);
        var plan = await JsonSerializer.DeserializeAsync<TransferPlan>(stream, AtomicJsonFile.Options, ct) ?? throw new InvalidDataException("传输计划损坏。");
        if (plan.PlanId != id || plan.Entries.Length > FileTransferPolicy.MaxEntries || plan.Direction is not ("upload" or "download")) throw new InvalidDataException("传输计划身份或结构不匹配。");
        return plan with { ArtifactDirectory = DirectoryFor(id) };
    }
}
