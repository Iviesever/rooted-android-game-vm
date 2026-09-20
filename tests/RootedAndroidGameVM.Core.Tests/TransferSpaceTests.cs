using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TransferSpaceTests
{
    [Fact]
    public void Substituted_drive_aliases_share_the_underlying_volume_identity()
    {
        var used = DriveInfo.GetDrives().Select(drive => drive.Name[..2]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alias = Enumerable.Range('R', 9).Select(value => (char)value + ":").First(letter => !used.Contains(letter));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rgvm-space-alias-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var mapped = false;
        int Subst(params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "subst.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            if (!process.WaitForExit(5000)) { process.Kill(); throw new TimeoutException("Isolated SUBST probe timed out."); }
            return process.ExitCode;
        }
        try
        {
            Assert.Equal(0, Subst(alias, root)); mapped = true;
            Assert.Equal(LocalTransferSpace.Observe(root).VolumeId, LocalTransferSpace.Observe(alias + @"\not-created").VolumeId);
            Assert.False(Directory.Exists(Path.Combine(root, "not-created")));
        }
        finally
        {
            var device = new System.Text.StringBuilder(32768);
            if (mapped && QueryDosDevice(alias, device, device.Capacity) > 0 && device.ToString().Equals(@"\??\" + root, StringComparison.OrdinalIgnoreCase))
                Assert.Equal(0, Subst(alias, "/d"));
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, System.Text.StringBuilder target, int length);

    private const long MiB = 1024 * 1024;
    private static TransferPlan Plan(string direction, string format, params TransferPlanEntry[] entries) =>
        new(new string('a', 32), DateTimeOffset.UtcNow, new string('b', 32), "1:2", direction, format, "live", [], null,
            direction == "upload" ? "files/target" : @"C:\export", entries, [], entries.Where(item => item.Source.Kind == "file").Sum(item => item.Source.Bytes),
            0, null, [], @"D:\records");
    private static TransferPlanEntry File(int index, long bytes, string conflict = "new", string? path = null) =>
        new(index, "s0", index + ".bin", path ?? index + ".bin", new("file", bytes, "v", "sha"), null, conflict);
    private static Dictionary<(string, string), TransferSpaceObservation> Observations(IEnumerable<TransferSpaceDemand> demands, bool sameHostVolume = false) =>
        demands.Select(d => (d.Endpoint, d.Path)).Distinct().ToDictionary(key => key, key => new TransferSpaceObservation(key.Endpoint, key.Path, key.Path,
            key.Endpoint == "guest" ? "guest-device" : sameHostVolume ? "host-one" : key.Path.StartsWith("C:", StringComparison.Ordinal) ? "host-C" : "host-D", 1024 * MiB, 4096));

    [Fact]
    public void Skipping_conflicting_directories_excludes_their_children_and_empty_directories_still_cost_space()
    {
        var directory = new TransferPlanEntry(0, "s0", "folder", "folder", new("directory", 0, "v", null), new("file", 3, "v", "old"), "type_conflict");
        var empty = new TransferPlanEntry(2, "s0", "empty", "empty", new("directory", 0, "v", null), null, "new");
        var plan = Plan("upload", "directory", directory, File(1, 900 * MiB, path: "folder/child.bin"), empty, File(3, MiB, "same"));
        var demands = TransferSpacePolicy.Demands(plan, "/data/app", policy: "skip");
        var guest = Assert.Single(demands, d => d.Endpoint == "guest");
        Assert.Equal(0, guest.Bytes); Assert.Equal(1, guest.Entries);
        Assert.DoesNotContain(demands, d => d.Purpose == "wire");
        Assert.Equal(4096, TransferSpacePolicy.Combine(demands, Observations(demands)).Single(c => c.Endpoint == "guest").RequiredBytes);
    }

    [Fact]
    public void An_offset_in_the_journal_does_not_earn_space_credit_without_verified_staging()
    {
        var plan = Plan("upload", "directory", File(0, 9 * MiB));
        var states = new Dictionary<int, TransferItemState> { [0] = new(0, "staging", "0.bin", Offset: 9 * MiB) };
        var untrusted = TransferSpacePolicy.Demands(plan, "/data/app", states);
        Assert.Equal(9 * MiB, untrusted.Single(d => d.Purpose == "destination").Bytes);
        var verified = TransferSpacePolicy.Demands(plan, "/data/app", states, new Dictionary<int, TransferSpaceProgress> { [0] = new(8 * MiB, true) });
        Assert.Equal(MiB, verified.Single(d => d.Purpose == "destination").Bytes);
        Assert.Equal(MiB, verified.Single(d => d.Endpoint == "guest" && d.Purpose == "wire").ScratchBytes);
    }

    [Fact]
    public void Tar_staged_content_does_not_remove_the_final_archive_allocation()
    {
        var plan = Plan("download", "tar", File(0, 9 * MiB), File(1, 2 * MiB));
        var progress = new Dictionary<int, TransferSpaceProgress> { [0] = new(9 * MiB, true, true), [1] = new(2 * MiB, true, true) };
        var demands = TransferSpacePolicy.Demands(plan, "", progress: progress);
        Assert.DoesNotContain(demands, d => d.Purpose is "wire" or "archive-cache");
        Assert.True(Assert.Single(demands, d => d.Purpose == "archive").Bytes > 11 * MiB);
        Assert.DoesNotContain(TransferSpacePolicy.Demands(plan, "", progress: progress, archiveComplete: true), d => d.Purpose == "archive");
    }

    [Fact]
    public void Tar_cache_and_archive_use_distinct_volumes_or_one_correct_phase_peak()
    {
        var plan = Plan("download", "tar", File(0, 9 * MiB));
        var demands = TransferSpacePolicy.Demands(plan, "");
        var separate = TransferSpacePolicy.Combine(demands, Observations(demands));
        Assert.Equal(2, separate.Length);
        var cache = separate.Single(c => c.VolumeId == "host-D"); var archive = separate.Single(c => c.VolumeId == "host-C");
        Assert.True(cache.GrowthBytes >= 17 * MiB); Assert.True(archive.GrowthBytes >= 9 * MiB);
        Assert.All(separate, c => Assert.Equal(512 * MiB, c.ReserveBytes));
        var merged = Assert.Single(TransferSpacePolicy.Combine(demands, Observations(demands, sameHostVolume: true)));
        Assert.True(merged.GrowthBytes >= 18 * MiB);
        Assert.True(merged.GrowthBytes < cache.GrowthBytes + archive.GrowthBytes); // Copy wire and archive output are different phases.
        Assert.Equal(512 * MiB, merged.ReserveBytes);
    }

    [Fact]
    public void Zero_work_does_not_claim_a_guest_buffer_and_distinct_scratch_purposes_are_additive()
    {
        var plan = Plan("upload", "directory", File(0, MiB, "same"));
        Assert.DoesNotContain(TransferSpacePolicy.Demands(plan, "/data/app"), d => d.Endpoint == "guest");
        TransferSpaceDemand[] demands = [new("host", @"D:\a", "wire", ScratchBytes: MiB), new("host", @"D:\b", "other-buffer", ScratchBytes: 2 * MiB)];
        Assert.Equal(3 * MiB, Assert.Single(TransferSpacePolicy.Combine(demands, Observations(demands))).GrowthBytes);
    }

    [Fact]
    public void Local_capacity_resolves_a_missing_destination_without_creating_it()
    {
        var path = Path.Combine(Path.GetTempPath(), "rgvm-space-" + Guid.NewGuid().ToString("N"), "missing", "nested");
        var actual = LocalTransferSpace.Observe(path);
        Assert.Equal("host", actual.Endpoint); Assert.True(actual.AvailableBytes > 0); Assert.True(actual.AllocationUnitBytes > 0);
        Assert.StartsWith(@"\\?\Volume{", actual.VolumeId); Assert.False(Directory.Exists(path));
        Assert.Throws<DebugException>(() => LocalTransferSpace.Require(path, actual.AvailableBytes));
    }

    [Fact]
    public void Archive_partial_reclaim_uses_observed_file_storage_and_missing_files_give_no_credit()
    {
        var path = Path.Combine(Path.GetTempPath(), "rgvm-archive-space-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, LocalTransferSpace.ReclaimableBytes(path));
            System.IO.File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(8192));
            Assert.InRange(LocalTransferSpace.ReclaimableBytes(path), 1, TransferSpacePolicy.RoundUp(8192, LocalTransferSpace.Observe(Path.GetDirectoryName(path)!).AllocationUnitBytes));
        }
        finally { System.IO.File.Delete(path); }
    }
}
