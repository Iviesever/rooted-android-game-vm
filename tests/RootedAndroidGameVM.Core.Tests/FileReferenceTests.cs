using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class FileReferenceTests
{
    private static FileRootIdentity Root => new("11223344556677889900aabbccddeeff", "123:456", 0, "private", "test.notes", new string('a', 64));

    [Fact]
    public void References_preserve_identity_and_android_names_without_using_windows_path_rules()
    {
        var original = new RemoteFileIdentity(Root, "files/中文 空格/line\nbreak\\name.txt", new string('b', 64));
        Assert.Equal(original, FileReferences.ReadEntry(FileReferences.Entry(original)));
        Assert.Equal(Root, FileReferences.ReadRoot(FileReferences.Root(Root)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/data/system")]
    [InlineData("files//two")]
    [InlineData("files/./two")]
    [InlineData("files/../two")]
    [InlineData("nul\0name")]
    public void Relative_paths_reject_traversal_before_device_access(string relative) =>
        Assert.Equal("path_escape", Assert.Throws<DebugException>(() => FileReferences.Relative(relative)).Code);

    [Fact]
    public void Root_kinds_cannot_smuggle_an_absolute_private_path_or_an_application_into_shared_scope()
    {
        Assert.Throws<DebugException>(() => FileReferences.Root(Root with { Volume = "/data/system" }));
        Assert.Throws<DebugException>(() => FileReferences.Root(Root with { Kind = "/data/system" }));
        Assert.Throws<DebugException>(() => FileReferences.Root(Root with { Kind = "shared", Volume = "/storage/emulated/0" }));
        var shared = new FileRootIdentity(Root.InstanceId, Root.Session, 0, "shared", Volume: "/storage/emulated/0");
        Assert.Equal(shared, FileReferences.ReadRoot(FileReferences.Root(shared)));
    }

    [Fact]
    public void Directory_cursor_is_bound_to_the_scope_session_and_path()
    {
        var cursor = new FilePageCursor(Root, "files", new string('c', 64), 500);
        var encoded = FileReferences.Cursor(cursor);
        Assert.Equal(cursor, FileReferences.ReadCursor(encoded, Root, "files"));
        Assert.Equal("stale_cursor", Assert.Throws<DebugException>(() => FileReferences.ReadCursor(encoded, Root with { Session = "123:457" }, "files")).Code);
        Assert.Equal("stale_cursor", Assert.Throws<DebugException>(() => FileReferences.ReadCursor(encoded, Root, "other")).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("entry1.e30")]
    [InlineData("entry1.bad*")]
    public void Malformed_entry_handles_are_rejected(string value) => Assert.Throws<DebugException>(() => FileReferences.ReadEntry(value));
}
