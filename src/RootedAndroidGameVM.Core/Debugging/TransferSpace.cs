using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record TransferSpaceDemand(string Endpoint, string Path, string Purpose, long Bytes = 0, int Entries = 0,
    long ScratchBytes = 0, int ScratchEntries = 0, string Phase = "all");
public sealed record TransferSpaceObservation(string Endpoint, string Path, string ResolvedPath, string VolumeId, long AvailableBytes, long AllocationUnitBytes);
public sealed record TransferStorageBinding(string Endpoint, string Path, string VolumeId);
public sealed record TransferSpaceCheck(string Endpoint, string VolumeId, long GrowthBytes, long ReserveBytes, long AvailableBytes,
    string[] Purposes, string[] Paths, int TotalPaths = 0)
{
    public long RequiredBytes => checked(GrowthBytes + ReserveBytes);
    public bool Sufficient => RequiredBytes <= AvailableBytes;
}
public sealed record TransferSpaceProgress(long PrefixBytes = 0, bool Exists = false, bool Complete = false);

public static class TransferSpacePolicy
{
    public const int ChunkBytes = 8 * 1024 * 1024;
    public const long HostReserveBytes = 512L * 1024 * 1024;
    public static long RoundUp(long bytes, long unit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(unit, 1);
        return bytes == 0 ? 0 : checked(((bytes - 1) / unit + 1) * unit);
    }
    private static bool IsSkipped(TransferPlanEntry item, IReadOnlyDictionary<int, TransferItemState> states, string policy, HashSet<string> skippedDirectories)
    {
        if (item.Conflict == "same" || states.GetValueOrDefault(item.Index)?.Status == "skipped" || policy == "skip" && item.Conflict is "different" or "type_conflict") return true;
        var path = item.TargetRelativePath;
        while (path.LastIndexOf('/') is var slash && slash >= 0)
        { path = path[..slash]; if (skippedDirectories.Contains(path)) return true; }
        return false;
    }

    public static TransferSpaceDemand[] Demands(TransferPlan plan, string guestRoot,
        IReadOnlyDictionary<int, TransferItemState>? states = null, IReadOnlyDictionary<int, TransferSpaceProgress>? progress = null,
        IReadOnlyDictionary<int, string>? effective = null, string policy = "overwrite", bool archiveComplete = false, long archiveReclaimableBytes = 0,
        int destinationDirectories = 0)
    {
        states ??= new Dictionary<int, TransferItemState>(); progress ??= new Dictionary<int, TransferSpaceProgress>();
        var skippedDirectories = plan.Entries.Where(item => item.Source.Kind == "directory" && (states.GetValueOrDefault(item.Index)?.Status == "skipped" ||
            policy == "skip" && item.Conflict is "different" or "type_conflict")).Select(item => item.TargetRelativePath).ToHashSet(StringComparer.Ordinal);
        var result = new List<TransferSpaceDemand>(); long wire = 0; var uploadFile = false;
        foreach (var item in plan.Entries)
        {
            if (item.Issue is not (null or "parent_type_conflict") || IsSkipped(item, states, policy, skippedDirectories)) continue;
            var observed = progress.GetValueOrDefault(item.Index) ?? new();
            if (observed.Complete) continue;
            var target = effective?.GetValueOrDefault(item.Index) ?? item.TargetRelativePath;
            var path = plan.Direction == "upload" ? FileTransferPolicy.JoinRemote(guestRoot,
                FileTransferPolicy.JoinRemote(plan.DestinationPath, target)) : plan.Format == "tar" ? Path.Combine(plan.ArtifactDirectory, "archive-content", item.Index + ".data") :
                Path.GetFullPath(Path.Combine(plan.DestinationPath, target.Replace('/', Path.DirectorySeparatorChar)));
            var parent = plan.Direction == "upload" ? path[..path.LastIndexOf('/')] : Path.GetDirectoryName(path)!;
            if (item.Source.Kind == "directory")
            {
                if (plan.Format == "tar" || observed.Exists || target == item.TargetRelativePath && item.Target?.Kind == "directory") continue;
                result.Add(new(plan.Direction == "upload" ? "guest" : "host", parent, "destination", Entries: 1));
            }
            else if (item.Source.Kind == "file")
            {
                if (observed.PrefixBytes < 0 || observed.PrefixBytes > item.Source.Bytes) throw new InvalidDataException("暂存抵扣超出文件长度。");
                var remaining = item.Source.Bytes - observed.PrefixBytes;
                result.Add(new(plan.Direction == "upload" ? "guest" : "host", parent, plan.Format == "tar" ? "archive-cache" : "destination",
                    remaining, observed.Exists ? 0 : 1));
                if (plan.Direction == "upload") uploadFile = true;
                wire = Math.Max(wire, Math.Min(ChunkBytes, remaining));
            }
        }
        // Host journals and tool evidence are separate from target data. The existing host reserve remains in force.
        result.Add(new("host", plan.ArtifactDirectory, "journal", Bytes: 65536, Entries: 1));
        if (wire > 0 || uploadFile)
            result.Add(new("host", plan.ArtifactDirectory, "wire", ScratchBytes: wire, ScratchEntries: 1, Phase: "copy"));
        if (plan.Direction == "upload" && uploadFile)
            result.Add(new("guest", "/data/local/tmp", "wire", ScratchBytes: wire, ScratchEntries: 2, Phase: "copy"));
        if (plan.Direction == "download" && destinationDirectories > 0)
            result.Add(new("host", plan.DestinationPath, "destination-root", Entries: destinationDirectories));
        if (plan.Direction == "download" && plan.Format == "tar" && !archiveComplete)
        {
            var archiveBytes = checked(plan.Entries.Where(item => item.Source.Kind == "file").Sum(item => RoundUp(item.Source.Bytes, 512)) + plan.Entries.Length * 16384L + 1024);
            result.Add(new("host", plan.DestinationPath, "archive", Math.Max(0, archiveBytes - archiveReclaimableBytes), Entries: 1, Phase: "archive"));
        }
        return result.ToArray();
    }
    public static TransferSpaceCheck[] Combine(IEnumerable<TransferSpaceDemand> demands, IReadOnlyDictionary<(string Endpoint, string Path), TransferSpaceObservation> observations)
    {
        return demands.GroupBy(demand => (demand.Endpoint, observations[(demand.Endpoint, demand.Path)].VolumeId))
            .Select(group =>
            {
                long peak = 0;
                foreach (var phase in group.Select(demand => demand.Phase).Where(phase => phase != "all").Append("idle").Distinct())
                {
                    var active = group.Where(demand => demand.Phase == "all" || demand.Phase == phase).ToArray();
                    long retained = 0;
                    foreach (var demand in active)
                    {
                        var unit = Math.Max(4096, observations[(demand.Endpoint, demand.Path)].AllocationUnitBytes);
                        retained = checked(retained + RoundUp(demand.Bytes, unit) + demand.Entries * unit);
                    }
                    var scratch = active.GroupBy(demand => demand.Purpose).Sum(purpose => purpose.Max(demand =>
                    {
                        var unit = Math.Max(4096, observations[(demand.Endpoint, demand.Path)].AllocationUnitBytes);
                        return checked(RoundUp(demand.ScratchBytes, unit) + demand.ScratchEntries * unit);
                    }));
                    peak = Math.Max(peak, checked(retained + scratch));
                }
                return new TransferSpaceCheck(group.Key.Endpoint, group.Key.VolumeId, peak, group.Key.Endpoint == "host" ? HostReserveBytes : 0,
                    group.Min(demand => observations[(demand.Endpoint, demand.Path)].AvailableBytes), group.Select(demand => demand.Purpose).Distinct().ToArray(),
                    group.Select(demand => demand.Path).Distinct().Take(12).ToArray(), group.Select(demand => demand.Path).Distinct().Count());
            }).ToArray();
    }
}

[SupportedOSPlatform("windows")]
public static class LocalTransferSpace
{
    public static TransferSpaceObservation Observe(string path)
    {
        StoragePathPolicy.RejectReparsePoints(path);
        var existing = Path.GetFullPath(path);
        while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new DebugException("space_unavailable", "找不到目标所在卷。", "verifying_space");
        using var handle = CreateFile(existing, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x02000000, 0);
        var physical = new StringBuilder(32768);
        var length = handle.IsInvalid ? 0 : GetFinalPathNameByHandle(handle, physical, physical.Capacity, 1); // VOLUME_NAME_GUID also resolves SUBST aliases.
        var name = physical.ToString(); var end = name.IndexOf("}\\", StringComparison.Ordinal);
        if (length == 0 || length >= physical.Capacity || end < 0 || !name.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
            throw new DebugException("space_unavailable", "无法识别实际本地卷：" + new Win32Exception(Marshal.GetLastWin32Error()).Message, "verifying_space", path);
        var volumeName = name[..(end + 2)];
        if (!GetDiskFreeSpaceEx(existing, out var available, out _, out _) || !GetDiskFreeSpace(volumeName, out var sectors, out var sectorBytes, out _, out _))
            throw new DebugException("space_unavailable", "无法读取实际本地卷容量：" + new Win32Exception(Marshal.GetLastWin32Error()).Message, "verifying_space", path);
        return new("host", path, existing, volumeName, checked((long)available), checked((long)sectors * sectorBytes));
    }
    public static long ReclaimableBytes(string file)
    {
        StoragePathPolicy.RejectReparsePoints(file);
        if (!File.Exists(file)) return 0;
        Marshal.SetLastPInvokeError(0);
        var low = GetCompressedFileSize(file, out var high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) throw new IOException("无法核对原归档暂存的实际占用。");
        return checked((long)(((ulong)high << 32) | low));
    }
    public static void Require(string path, long growth)
    {
        var observation = Observe(path);
        if (growth < 0 || growth > observation.AvailableBytes - TransferSpacePolicy.HostReserveBytes)
            throw new DebugException("insufficient_space", "本地卷空间不足，包含512MiB保留余量：" + path, "verifying_space");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, FileShare share, nint security, FileMode creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetCompressedFileSize(string fileName, out uint high);
}
