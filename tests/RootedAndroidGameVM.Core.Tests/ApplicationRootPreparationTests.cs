using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ApplicationRootPreparationTests
{
    private static TransferPlan Plan(params TransferPlanEntry[] entries) => new(new string('a', 32), DateTimeOffset.UtcNow, new string('b', 32), "1:2", "upload",
        "directory", "live", [], new(new string('b', 32), "1:2", 10, "external", "test.app", new string('c', 64), "/storage/emulated/10"),
        "", entries, [], 0, 0, null, [], "artifact", DestinationAnchor: "/storage/emulated/10");
    [Fact]
    public void Only_declared_parent_preparation_and_the_selected_application_are_allowed()
    {
        var plan = Plan(FileTransferPolicy.PlannedDirectories("Android/data/test.app").ToArray());
        foreach (var path in new[] { "Android", "Android/data", "Android/data/test.app", "Android/data/test.app/files/nested/a.txt" })
            FileTransferPolicy.ValidateAnchoredTarget(plan, path);
        foreach (var path in new[] { "", "Download/a.txt", "Android/data/other.app/file", "Android/data/test.app/../other.app", "Android/obb/test.app" })
            Assert.Throws<DebugException>(() => FileTransferPolicy.ValidateAnchoredTarget(plan, path));
        Assert.Throws<DebugException>(() => FileTransferPolicy.ValidateAnchoredTarget(Plan(), "Android/data"));
        Assert.Throws<DebugException>(() => FileTransferPolicy.ValidateAnchoredTarget(plan with { DestinationAnchor = "/storage/emulated/0" }, "Android/data/test.app/file"));
    }
    [Fact]
    public void Shared_preparation_ancestors_never_receive_application_ownership()
    {
        var plan = Plan(FileTransferPolicy.PlannedDirectories("Android/data/test.app").ToArray());
        Assert.False(FileTransferPolicy.RequiresApplicationOwner(plan, "Android"));
        Assert.False(FileTransferPolicy.RequiresApplicationOwner(plan, "Android/data"));
        Assert.True(FileTransferPolicy.RequiresApplicationOwner(plan, "Android/data/test.app"));
        Assert.True(FileTransferPolicy.RequiresApplicationOwner(plan, "Android/data/test.app/files/file.txt"));
        Assert.True(FileTransferPolicy.RequiresApplicationOwner(plan with { DestinationAnchor = null }, "files/file.txt"));
        Assert.False(FileTransferPolicy.RequiresApplicationOwner(plan with { DestinationAnchor = null, DestinationRoot = plan.DestinationRoot! with { Kind = "shared" } }, "file.txt"));
        Assert.False(FileTransferPolicy.RequiresApplicationOwner(plan with { DestinationAnchor = null, DestinationRoot = plan.DestinationRoot! with { Kind = "private" } }, "files/file.txt"));
    }
    [Theory]
    [InlineData("external", "Android/data/test.app")]
    [InlineData("obb", "Android/obb/test.app")]
    [InlineData("media", "Android/media/test.app")]
    public void Preparation_prefix_comes_from_the_discovered_application_identity(string kind, string prefix) =>
        Assert.Equal(prefix, FileTransferPolicy.ApplicationStoragePrefix(Plan().DestinationRoot! with { Kind = kind }));
    [Theory]
    [InlineData("private")]
    [InlineData("device-private")]
    [InlineData("shared")]
    public void System_prepared_or_shared_roots_cannot_be_created_as_application_roots(string kind) =>
        Assert.Throws<DebugException>(() => FileTransferPolicy.ApplicationStoragePrefix(Plan().DestinationRoot! with { Kind = kind }));
}
