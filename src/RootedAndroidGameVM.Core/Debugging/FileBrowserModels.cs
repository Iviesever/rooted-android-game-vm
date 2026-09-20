using System.Text.Json;
using System.Text.RegularExpressions;
using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record AndroidUser(int UserId, string Name, bool Unlocked);
public sealed record AndroidStorageVolume(string Path, bool Primary, string? AccessPath = null);
public sealed record AndroidUserStorage(int UserId, string Name, bool Unlocked, AndroidStorageVolume[] Volumes);
public sealed record FileRootIdentity(string InstanceId, string Session, int UserId, string Kind,
    string? Package = null, string? InstallationRevision = null, string? Volume = null);
public sealed record RemoteFileIdentity(FileRootIdentity Root, string RelativePath, string Version);
public sealed record FileRootDescriptor(string RootRef, string Kind, string Title, string DisplayPath,
    bool Exists, bool Accessible, bool Writable, bool Locked, string? Reason, string? EntryRef, bool Creatable = false, string? AccessPath = null);
public sealed record RemoteFileEntry(string Name, string RelativePath, string Kind, long Bytes, long ModifiedUnixMs,
    int Uid, int Gid, string Mode, string Version, string? LinkTarget = null, string? Sha256 = null, string? EntryRef = null);
public sealed record FileBrowsePage(string RootRef, string RelativePath, RemoteFileEntry Directory,
    RemoteFileEntry[] Entries, int Total, string? NextCursor, string Snapshot, DateTimeOffset ObservedAt);
public sealed record FilePageCursor(FileRootIdentity Root, string RelativePath, string Snapshot, int Offset);

public static class FileReferences
{
    public static string VolumeAccessPath(AndroidStorageVolume volume, int user)
    {
        if (volume.AccessPath is null) return volume.Path;
        if (user < 0 || !volume.Path.StartsWith("/storage/", StringComparison.Ordinal))
            throw new DebugException("path_escape", "卷访问视图缺少有效用户或逻辑卷。", "resolving_file");
        var relative = volume.Path["/storage/".Length..]; Relative(relative);
        if (relative.Length == 0 || volume.AccessPath != "/mnt/user/" + user + "/" + relative)
            throw new DebugException("path_escape", "卷访问视图不属于所选用户和卷。", "resolving_file");
        return volume.AccessPath;
    }
    public static readonly string[] Kinds = ["private", "device-private", "external", "obb", "media", "shared"];
    private static string Encode<T>(string prefix, T value) => prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, DebugJson.Options))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static T Decode<T>(string prefix, string value)
    {
        try
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length > 65536) throw new ArgumentException();
            var encoded = value[prefix.Length..].Replace('-', '+').Replace('_', '/');
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')), DebugJson.Options)
                ?? throw new ArgumentException();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
        { throw new DebugException("invalid_file_reference", "文件引用无效，请重新获取根目录或目录条目。", "resolving_file"); }
    }
    public static string Root(FileRootIdentity identity) { Validate(identity); return Encode("root1.", identity); }
    public static string Entry(RemoteFileIdentity identity) { Validate(identity.Root); Relative(identity.RelativePath); Version(identity.Version); return Encode("entry1.", identity); }
    public static FileRootIdentity ReadRoot(string value) { var identity = Decode<FileRootIdentity>("root1.", value); Validate(identity); return identity; }
    public static RemoteFileIdentity ReadEntry(string value)
    {
        var identity = Decode<RemoteFileIdentity>("entry1.", value);
        Validate(identity.Root); Relative(identity.RelativePath); Version(identity.Version); return identity;
    }
    public static string Cursor(FilePageCursor cursor) => Encode("page1.", cursor);
    public static FilePageCursor ReadCursor(string value, FileRootIdentity root, string relative)
    {
        var cursor = Decode<FilePageCursor>("page1.", value);
        if (cursor.Root != root || cursor.RelativePath != relative || cursor.Offset < 0)
            throw new DebugException("stale_cursor", "游标不属于当前目录。", "browsing_files");
        Version(cursor.Snapshot);
        return cursor;
    }
    public static void Relative(string relative)
    {
        if (relative is null || relative.Length > 4096 || relative.StartsWith('/') || relative.Contains('\0') ||
            relative.Length > 0 && relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new DebugException("path_escape", "路径必须是根目录内的相对路径。", "resolving_file");
    }
    private static void Version(string value)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new DebugException("invalid_file_reference", "文件版本引用无效。", "resolving_file");
    }
    public static void Validate(FileRootIdentity identity)
    {
        if (identity is null || !Guid.TryParseExact(identity.InstanceId, "N", out _) || identity.UserId < 0 ||
            identity.Session is null || !Regex.IsMatch(identity.Session, "^[0-9]+:[0-9]+$") || !Kinds.Contains(identity.Kind))
            throw new DebugException("invalid_file_reference", "根目录身份无效。", "resolving_file");
        if (identity.Kind == "shared")
        {
            if (identity.Package is not null || identity.InstallationRevision is not null)
                throw new DebugException("invalid_file_reference", "共享目录不能混用应用身份。", "resolving_file");
        }
        else
        {
            try { AndroidPackageName.Parse(identity.Package!); }
            catch (ArgumentException) { throw new DebugException("invalid_file_reference", "应用目录缺少有效包名。", "resolving_file"); }
            Version(identity.InstallationRevision!);
        }
        if (identity.Kind is "private" or "device-private")
        {
            if (identity.Volume is not null) throw new DebugException("invalid_file_reference", "私有目录没有外部卷参数。", "resolving_file");
        }
        else if (string.IsNullOrEmpty(identity.Volume) || !identity.Volume.StartsWith('/') || identity.Volume.Contains('\0'))
            throw new DebugException("invalid_file_reference", "外部卷身份无效。", "resolving_file");
    }
}
