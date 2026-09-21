using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class FileTransferPlanningTests
{
    [Theory]
    [InlineData(122, 8)]
    [InlineData(25, 700)]
    public void Target_batches_allow_observation_results_to_replace_original_plan_items(int count, int nameLength)
    {
        var items = Enumerable.Range(0, count).Select(index => new TransferPlanEntry(index, "s0", "", new string('x', nameLength) + index,
            new("file", 1, "source", "sha"), null, "new")).ToList();
        var method = typeof(AndroidDebugService).GetMethod("RemoteTargetBatches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var batches = (IEnumerable<TransferPlanEntry[]>)method.Invoke(null, [items.Where(item => item.Issue is null), "parent"])!;
        var visited = new List<int>(); var batchCount = 0;
        foreach (var batch in batches)
        {
            batchCount++;
            Assert.InRange(batch.Length, 1, 100);
            foreach (var item in batch)
            {
                visited.Add(item.Index);
                items[item.Index] = item with { Conflict = "same", Target = item.Source };
            }
        }
        Assert.True(batchCount > 1);
        Assert.Equal(Enumerable.Range(0, count), visited);
        Assert.All(items, item => Assert.Equal("same", item.Conflict));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("nested/file")]
    [InlineData("/absolute")]
    [InlineData("")]
    public void Renaming_a_source_cannot_change_its_destination_directory(string name) =>
        Assert.ThrowsAny<Exception>(() => FileTransferPolicy.TargetName(name));

    [Fact]
    public async Task Planned_parent_directories_survive_storage_and_old_plans_remain_readable()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgvm-parent-plan-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var directories = FileTransferPolicy.PlannedDirectories("Download/中文 空格/empty");
            Assert.Equal(new[] { "Download", "Download/中文 空格", "Download/中文 空格/empty" }, directories.Select(item => item.TargetRelativePath));
            Assert.All(directories, item => Assert.True(item.CreateDirectory));
            Assert.Empty(FileTransferPolicy.PlannedDirectories(""));
            var store = new TransferPlanStore(root);
            var plan = new TransferPlan(id, DateTimeOffset.UtcNow, new string('a', 32), "123:456", "upload", "directory", "live",
                [], null, "", directories, [], 0, 12288, null, [], store.DirectoryFor(id));
            await store.SaveAsync(plan, default);
            Assert.Equal(directories, (await store.ReadAsync(id, default)).Entries);
            var legacy = System.Text.Json.JsonSerializer.Deserialize<TransferPlanEntry>(
                "{\"index\":0,\"selectionId\":\"s0\",\"relativePath\":\"\",\"targetRelativePath\":\"a\",\"source\":{\"kind\":\"file\",\"bytes\":1,\"version\":\"v\"},\"conflict\":\"new\"}", DebugJson.Options)!;
            Assert.False(legacy.CreateDirectory);
            Assert.False(Directory.Exists(Path.Combine(root, "Download")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
