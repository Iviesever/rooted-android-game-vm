using System.Text.Json;
using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Storage;

public sealed record ResourceMigrationResult(string ResourceRoot, long TransferredBytes, long ReclaimedBytes,
    IReadOnlyList<string> RemainingSourceFiles)
{
    public bool HasPendingCleanup => RemainingSourceFiles.Count > 0;
}

public sealed class ResourceMigrationService
{
    public const string ReceiptFileName = "storage-last-migration.json";
    private const string ReplicaMarker = ".rgvm-migration-target.json";
    private readonly ProductStorageLocation _location;
    private readonly Func<InstallPaths, CancellationToken, Task> _stop;
    private readonly Func<InstallPaths, InstallPaths, CancellationToken, Task> _relocate;
    private readonly Func<InstallPaths, CancellationToken, Task> _verify;
    private string JournalPath => Path.Combine(_location.ControlRoot, MigrationJournal.FileName);

    public ResourceMigrationService(ProductStorageLocation? location = null,
        Func<InstallPaths, CancellationToken, Task>? stop = null,
        Func<InstallPaths, InstallPaths, CancellationToken, Task>? relocate = null,
        Func<InstallPaths, CancellationToken, Task>? verify = null)
    {
        _location = location ?? new ProductStorageLocation();
        var runtime = new AndroidStorageRuntime();
        _stop = stop ?? runtime.StopAsync;
        _relocate = relocate ?? new StoragePathRelocator().RelocateAsync;
        _verify = verify ?? runtime.VerifyAsync;
    }

    public Task<ResourceMigrationResult> MigrateAsync(string target, IProgress<ResourceTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => MigrateCoreAsync(target, progress, cancellationToken), cancellationToken);

    private async Task<ResourceMigrationResult> MigrateCoreAsync(string target, IProgress<ResourceTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var lease = StorageOperationLease.Acquire(_location);
        if (MigrationJournal.Read(_location) is not null)
            throw new InvalidOperationException("请先完成上次迁移的恢复或原目录清理。");
        var source = _location.ReadRoot();
        target = StoragePathPolicy.NormalizeRoot(target);
        ValidatePair(source, target);
        StorageOwnership.AssertSafeRoot(source, _location.ControlRoot);
        if (!StorageOwnership.IsOwned(source)) throw new InvalidDataException("原目录不是可识别的产品资源目录。");
        if (File.Exists(target) || Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("目标文件夹必须为空，不会覆盖已有文件。");
        var sourcePaths = InstallPaths.FromProductRoot(source);
        var targetPaths = InstallPaths.FromProductRoot(target);
        progress?.Report(new("正在停止虚拟机", 0, 0, "等待产品虚拟机和文件操作退出。"));
        await _stop(sourcePaths, cancellationToken).ConfigureAwait(false);
        var original = VerifiedDirectoryCopy.ReadInventory(source, StorageOwnership.ControlFiles);
        var id = Guid.NewGuid();
        var staging = Path.Combine(Path.GetDirectoryName(target)!, ".rgvm-migration-" + id.ToString("N"));
        var journal = new MigrationJournal(id, source, target, staging, MigrationStage.Copying, original);
        await SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);

        var copy = await new VerifiedDirectoryCopy().CopyAsync(source, staging, StorageOwnership.ControlFiles,
            progress, cancellationToken).ConfigureAwait(false);
        AssertSameInventory(original, copy.Inventory);
        journal = journal with { Inventory = copy.Inventory };
        await SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);
        await AtomicJsonFile.WriteAsync(Path.Combine(staging, ReplicaMarker), new ReplicaIdentity(id), cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(target)) Directory.Delete(target); // Only the previously validated empty directory.
        Directory.Move(staging, target);
        progress?.Report(new("正在调整资源路径", original.TotalBytes, original.TotalBytes, "保留磁盘数据，更新虚拟机注册信息。"));
        await _relocate(sourcePaths, targetPaths, cancellationToken).ConfigureAwait(false);
        await StorageOwnership.MarkOwnedAsync(target, cancellationToken).ConfigureAwait(false);
        journal = journal with { Stage = MigrationStage.Prepared };
        await SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("正在验证新位置", original.TotalBytes, original.TotalBytes, "冷启动并检查 Root，验证完成前保留原资源。"));
        try
        {
            await _verify(targetPaths, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _stop(targetPaths, CancellationToken.None).ConfigureAwait(false);
        }
        AssertSameInventory(copy.Inventory, VerifiedDirectoryCopy.ReadInventory(source, StorageOwnership.ControlFiles));
        cancellationToken.ThrowIfCancellationRequested();
        journal = journal with { Stage = MigrationStage.Verified };
        await SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);
        // Once verified is durable, recovery finishes the switch; it never falls back after source cleanup starts.
        await _location.SaveRootAsync(target, CancellationToken.None).ConfigureAwait(false);
        journal = journal with { Stage = MigrationStage.Cleaning };
        await SaveJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
        return await CleanupSourceAsync(journal, progress, CancellationToken.None).ConfigureAwait(false);
    }

    public Task<ResourceMigrationResult> RecoverAsync(IProgress<ResourceTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RecoverCoreAsync(progress, cancellationToken), cancellationToken);

    private async Task<ResourceMigrationResult> RecoverCoreAsync(IProgress<ResourceTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var lease = StorageOperationLease.Acquire(_location, allowRecovery: true);
        var journal = MigrationJournal.Read(_location);
        if (journal is null) return new(_location.ReadRoot(), 0, 0, []);
        ValidateJournal(journal);
        if (journal.Stage is MigrationStage.Verified or MigrationStage.Cleaning)
        {
            AssertReplicaIdentity(journal.TargetRoot, journal.Id);
            await _location.SaveRootAsync(journal.TargetRoot, cancellationToken).ConfigureAwait(false);
            journal = journal with { Stage = MigrationStage.Cleaning };
            await SaveJournalAsync(journal, cancellationToken).ConfigureAwait(false);
            await _stop(InstallPaths.FromProductRoot(journal.SourceRoot), cancellationToken).ConfigureAwait(false);
            return await CleanupSourceAsync(journal, progress, cancellationToken).ConfigureAwait(false);
        }

        if (!StorageOwnership.IsOwned(journal.SourceRoot))
            throw new InvalidDataException("无法确认原资源目录，已保留迁移副本。");
        progress?.Report(new("正在恢复原位置", 0, 0, "原始数据保持不变，清理未提交的迁移副本。"));
        if (Directory.Exists(journal.TargetRoot) && Directory.EnumerateFileSystemEntries(journal.TargetRoot).Any())
        {
            AssertReplicaIdentity(journal.TargetRoot, journal.Id);
            await _stop(InstallPaths.FromProductRoot(journal.TargetRoot), cancellationToken).ConfigureAwait(false);
            DeleteReplica(journal.TargetRoot);
        }
        if (Directory.Exists(journal.StagingRoot)) DeleteReplica(journal.StagingRoot);
        await _location.SaveRootAsync(journal.SourceRoot, cancellationToken).ConfigureAwait(false);
        File.Delete(JournalPath);
        return new(journal.SourceRoot, 0, 0, []);
    }

    private async Task<ResourceMigrationResult> CleanupSourceAsync(MigrationJournal journal,
        IProgress<ResourceTransferProgress>? progress, CancellationToken cancellationToken)
    {
        ValidateJournal(journal);
        AssertReplicaIdentity(journal.TargetRoot, journal.Id);
        var remaining = new List<string>();
        long reclaimed = 0;
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { StorageOwnership.MarkerName, "install.json", "install-state.json" };
        var ordered = journal.Inventory.Files.OrderBy(file => identities.Contains(file.RelativePath)).ToList();
        if (ordered.Any(file => File.Exists(Path.Combine(journal.SourceRoot, file.RelativePath))) &&
            !StorageOwnership.IsOwned(journal.SourceRoot))
            throw new InvalidDataException("原目录的产品标识不可用，已保留剩余文件。");
        progress?.Report(new("正在清理原资源", 0, journal.Inventory.TotalBytes, "新位置已验证，逐项核验并释放原磁盘空间。"));
        foreach (var file in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathBoundary.EnsureWithinRoot(journal.SourceRoot, Path.Combine(journal.SourceRoot, file.RelativePath));
            if (!File.Exists(path)) continue;
            if (identities.Contains(file.RelativePath) && remaining.Count > 0)
            {
                remaining.Add(file.RelativePath);
                continue;
            }
            try
            {
                StoragePathPolicy.RejectReparsePoints(path);
                var hash = await Sha256Verifier.ComputeAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    remaining.Add(file.RelativePath);
                    continue;
                }
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.Delete(path);
                reclaimed += file.Length;
                progress?.Report(new("正在清理原资源", reclaimed, journal.Inventory.TotalBytes, file.RelativePath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                remaining.Add(file.RelativePath);
            }
        }
        foreach (var relative in journal.Inventory.Directories.OrderByDescending(path => path.Length))
        {
            var directory = PathBoundary.EnsureWithinRoot(journal.SourceRoot, Path.Combine(journal.SourceRoot, relative));
            StoragePathPolicy.RejectReparsePoints(directory);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        if (Directory.Exists(journal.SourceRoot))
        {
            remaining.AddRange(VerifiedDirectoryCopy.ReadInventory(journal.SourceRoot, StorageOwnership.ControlFiles)
                .Files.Select(file => file.RelativePath));
            if (!string.Equals(journal.SourceRoot, _location.ControlRoot, StringComparison.OrdinalIgnoreCase) &&
                !Directory.EnumerateFileSystemEntries(journal.SourceRoot).Any()) Directory.Delete(journal.SourceRoot);
        }
        remaining = remaining.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        reclaimed = journal.Inventory.Files
            .Where(file => !File.Exists(Path.Combine(journal.SourceRoot, file.RelativePath))).Sum(file => file.Length);
        await AtomicJsonFile.WriteAsync(Path.Combine(_location.ControlRoot, ReceiptFileName), new
        {
            migrationId = journal.Id, sourceRoot = journal.SourceRoot, resourceRoot = journal.TargetRoot,
            checkedAtUtc = DateTimeOffset.UtcNow, transferredBytes = journal.Inventory.TotalBytes,
            reclaimedBytes = reclaimed, remainingSourceFiles = remaining, verifiedFiles = journal.Inventory.Files
        }, cancellationToken).ConfigureAwait(false);
        if (remaining.Count == 0)
        {
            File.Delete(JournalPath);
            File.Delete(Path.Combine(journal.TargetRoot, ReplicaMarker));
        }
        return new(journal.TargetRoot, journal.Inventory.TotalBytes, reclaimed, remaining);
    }

    private void ValidatePair(string source, string target)
    {
        StorageOwnership.AssertSafeRoot(target, _location.ControlRoot);
        if (StoragePathPolicy.Contains(source, target) || StoragePathPolicy.Contains(target, source))
            throw new ArgumentException("原目录与目标目录不能相同或互相包含。");
    }

    private void ValidateJournal(MigrationJournal journal)
    {
        if (journal.Id == Guid.Empty || !Enum.IsDefined(journal.Stage)) throw new InvalidDataException("迁移记录无效。");
        StorageOwnership.AssertSafeRoot(journal.SourceRoot, _location.ControlRoot);
        ValidatePair(journal.SourceRoot, journal.TargetRoot);
        var expectedStaging = Path.Combine(Path.GetDirectoryName(journal.TargetRoot)!, ".rgvm-migration-" + journal.Id.ToString("N"));
        if (!string.Equals(expectedStaging, journal.StagingRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("迁移临时目录与记录不一致。");
        StoragePathPolicy.RejectReparsePoints(journal.StagingRoot);
        foreach (var file in journal.Inventory.Files)
            PathBoundary.EnsureWithinRoot(journal.SourceRoot, Path.Combine(journal.SourceRoot, file.RelativePath));
    }

    private static void AssertSameInventory(ResourceInventory expected, ResourceInventory current)
    {
        var before = expected.Files.Select(file => (file.RelativePath, file.Length, file.LastWriteTimeUtc));
        var after = current.Files.Select(file => (file.RelativePath, file.Length, file.LastWriteTimeUtc));
        if (!before.SequenceEqual(after) || !expected.Directories.Order().SequenceEqual(current.Directories.Order()))
            throw new IOException("迁移期间原目录发生变化；已保留原目录，尚未切换位置。");
    }

    private static void AssertReplicaIdentity(string root, Guid id)
    {
        StoragePathPolicy.RejectReparsePoints(root);
        var path = Path.Combine(root, ReplicaMarker);
        if (!File.Exists(path)) throw new InvalidDataException("无法确认迁移副本归属，已停止清理。");
        StoragePathPolicy.RejectReparsePoints(path);
        var marker = JsonSerializer.Deserialize<ReplicaIdentity>(File.ReadAllText(path), AtomicJsonFile.Options);
        if (marker?.Id != id) throw new InvalidDataException("迁移副本标识不匹配。");
    }

    private static void DeleteReplica(string path)
    {
        var inventory = VerifiedDirectoryCopy.ReadInventory(path);
        foreach (var file in inventory.Files)
        {
            var fullPath = PathBoundary.EnsureWithinRoot(path, Path.Combine(path, file.RelativePath));
            File.SetAttributes(fullPath, File.GetAttributes(fullPath) & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }

    private Task SaveJournalAsync(MigrationJournal journal, CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(JournalPath, journal, cancellationToken);

    private sealed record ReplicaIdentity(Guid Id);
}
