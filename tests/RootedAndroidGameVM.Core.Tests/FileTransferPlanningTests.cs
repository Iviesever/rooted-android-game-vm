using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class FileTransferPlanningTests
{
    [Theory]
    [InlineData("docs/正常名称.txt", null)]
    [InlineData("docs/😀.txt", null)]
    [InlineData("docs/CON.txt", "windows_name_unsupported")]
    [InlineData("COM¹.txt", "windows_name_unsupported")]
    [InlineData("docs/trailing. ", "windows_name_unsupported")]
    [InlineData("docs/line\nbreak", "windows_name_unsupported")]
    [InlineData("docs/back\\slash", "windows_name_unsupported")]
    public void Names_that_windows_cannot_preserve_are_reported_before_copy(string path, string? issue) =>
        Assert.Equal(issue, FileTransferPolicy.WindowsNameIssue(path));

    [Fact]
    public async Task File_equality_uses_content_and_local_inspection_never_creates_targets()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "a.bin"); var b = Path.Combine(root, "b.bin");
            await File.WriteAllBytesAsync(a, [0, 1, 2, 255]); await File.WriteAllBytesAsync(b, [0, 1, 2, 254]);
            var source = (await FileTransferPolicy.LocalFingerprintAsync(a, default))!;
            var target = (await FileTransferPolicy.LocalFingerprintAsync(b, default))!;
            Assert.Equal("different", FileTransferPolicy.Conflict(source, target));
            Assert.Equal("same", FileTransferPolicy.Conflict(source, source with { Version = "a different timestamp" }));
            Assert.Equal("type_conflict", FileTransferPolicy.Conflict(source, new("directory", 0, "v", null)));
            var untouched = FileTransferPolicy.LocalTarget(Path.Combine(root, "not-created"), "empty/target.bin");
            Assert.Null(await FileTransferPolicy.LocalFingerprintAsync(untouched, default));
            Assert.False(Directory.Exists(Path.Combine(root, "not-created")));
            Assert.Throws<DebugException>(() => FileTransferPolicy.LocalTarget(root, "../escape"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_saved_plan_is_inspectable_after_restart_and_remains_unexecuted()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-plan-store-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var store = new TransferPlanStore(root);
            var entry = new TransferPlanEntry(0, "s0", "", "empty", new("directory", 0, "v", null), null, "new");
            var plan = new TransferPlan(id, DateTimeOffset.UtcNow, new string('a', 32), "123:456", "download", "directory", "live",
                [new("s0", "empty", null, "directory", "empty", "v")], null, Path.Combine(root, "export"), [entry], [], 0, 0, null, [], store.DirectoryFor(id));
            await store.SaveAsync(plan, default);
            var loaded = await new TransferPlanStore(root).ReadAsync(id, default);
            Assert.Equal("planned", loaded.Status); Assert.False(loaded.TransferVerified);
            Assert.Equal("directory", Assert.Single(loaded.Entries).Source.Kind);
            Assert.False(Directory.Exists(plan.DestinationPath));
            var card = Assert.Single(await new TransferPlanStore(root).ListAsync(default));
            Assert.Equal(id, card.PlanId); Assert.Equal(1, card.TotalEntries); Assert.Equal("planned", card.Status);
            File.Delete(Path.Combine(store.DirectoryFor(id), "summary.json"));
            var migrated = Assert.Single(await new TransferPlanStore(root).ListAsync(default));
            Assert.Equal(card, migrated); Assert.True(File.Exists(Path.Combine(store.DirectoryFor(id), "summary.json")));
            Assert.Throws<ArgumentException>(() => store.DirectoryFor("../outside"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
