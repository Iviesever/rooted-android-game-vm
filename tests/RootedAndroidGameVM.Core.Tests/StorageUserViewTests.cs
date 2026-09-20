using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class StorageUserViewTests
{
    [Fact]
    public void Direct_volume_paths_keep_the_existing_reference_identity()
    {
        Assert.Equal("/storage/emulated/0", FileReferences.VolumeAccessPath(new("/storage/emulated/0", true), 0));
        Assert.Equal("/mnt/user/10/emulated/10", FileReferences.VolumeAccessPath(new("/storage/emulated/10", true, "/mnt/user/10/emulated/10"), 10));
        Assert.Equal("/mnt/user/10/ABCD-1234", FileReferences.VolumeAccessPath(new("/storage/ABCD-1234", false, "/mnt/user/10/ABCD-1234"), 10));
    }
    [Theory]
    [InlineData("/mnt/user/0/emulated/10")]
    [InlineData("/mnt/user/10/emulated/0")]
    [InlineData("/mnt/user/10/other")]
    [InlineData("/data/media/10")]
    [InlineData("/mnt/user/10/emulated/10/../0")]
    public void Views_from_other_users_volumes_or_raw_storage_are_rejected(string access) =>
        Assert.Throws<DebugException>(() => FileReferences.VolumeAccessPath(new("/storage/emulated/10", true, access), 10));

    [Fact]
    public void A_view_cannot_smuggle_parent_traversal_through_the_logical_volume() =>
        Assert.Throws<DebugException>(() => FileReferences.VolumeAccessPath(new("/storage/../data/media/10", true, "/mnt/user/10/../data/media/10"), 10));
}
