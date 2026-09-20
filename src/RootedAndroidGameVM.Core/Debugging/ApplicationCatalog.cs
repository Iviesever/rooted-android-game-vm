using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record CatalogApplication(string AppRef, string Package, string Name, string NameSource, int UserId, int Uid,
    string VersionName, long VersionCode, bool System, bool Enabled, string InstallationRevision, int[] RunningPids,
    string? RunningStateError, string? PrivateDirectory, string? CredentialProtectedDirectory, string? DeviceProtectedDirectory,
    string? IconPath, string? MetadataError);
public sealed record ApplicationCatalogSnapshot(string InstanceId, string Session, int UserId, string Locale,
    DateTimeOffset ObservedAt, CatalogApplication[] Entries, string SnapshotId);
public sealed record ApplicationCatalogPage(string InstanceId, string Session, int UserId, string Locale,
    DateTimeOffset ObservedAt, CatalogApplication[] Entries, int Total, string? NextCursor, string SnapshotId);
public sealed record ApplicationReference(string InstanceId, int UserId, string Package, string InstallationRevision);

public static class ApplicationCatalog
{
    private sealed record PageCursor(string Snapshot, string Query, int Offset);
    private static string Encode<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, DebugJson.Options))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static T Decode<T>(string value)
    {
        if (value.Length > 4096) throw new ArgumentException("引用过长。");
        value = value.Replace('-', '+').Replace('_', '/');
        return JsonSerializer.Deserialize<T>(Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')), DebugJson.Options)
            ?? throw new ArgumentException("引用为空。");
    }
    public static string Reference(ApplicationReference identity) => "app1." + Encode(identity);
    public static ApplicationReference ParseReference(string appRef)
    {
        try
        {
            if (!appRef.StartsWith("app1.", StringComparison.Ordinal)) throw new ArgumentException("版本不匹配。");
            var identity = Decode<ApplicationReference>(appRef[5..]);
            if (!Guid.TryParseExact(identity.InstanceId, "N", out _) || identity.UserId < 0 ||
                identity.InstallationRevision?.Length != 64 || !identity.InstallationRevision.All(Uri.IsHexDigit))
                throw new ArgumentException("身份无效。");
            AndroidPackageName.Parse(identity.Package);
            return identity;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
        { throw new DebugException("invalid_app_reference", "应用引用无效，请重新调用apps.list。", "resolving_application", inner: error); }
    }
    public static string Revision(string package, int user, int uid, long installedAt, long updatedAt, long version, string sourceDir) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { package, user, uid, installedAt, updatedAt, version, sourceDir }))).ToLowerInvariant();

    public static ApplicationCatalogPage Page(ApplicationCatalogSnapshot snapshot, string query, int pageSize, string cursor)
    {
        if (pageSize is < 1 or > 200 || query.Length > 256) throw new ArgumentException("pageSize须为1–200，query最多256字符。");
        query = query.Trim();
        var offset = 0;
        if (cursor.Length > 0)
        {
            try
            {
                var page = Decode<PageCursor>(cursor);
                if (page.Snapshot != snapshot.SnapshotId || page.Query != query || page.Offset < 0) throw new ArgumentException();
                offset = page.Offset;
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
            { throw new DebugException("stale_cursor", "应用清单或查询已改变，请从第一页重新读取。", "listing_applications"); }
        }
        var found = snapshot.Entries.Where(app => app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            app.Package.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.Package, StringComparer.Ordinal).ToArray();
        if (offset > found.Length) throw new DebugException("stale_cursor", "清单游标越界。", "listing_applications");
        var entries = found.Skip(offset).Take(pageSize).ToArray();
        var next = offset + entries.Length < found.Length ? Encode(new PageCursor(snapshot.SnapshotId, query, offset + entries.Length)) : null;
        return new(snapshot.InstanceId, snapshot.Session, snapshot.UserId, snapshot.Locale, snapshot.ObservedAt, entries, found.Length, next, snapshot.SnapshotId);
    }
    public static CatalogApplication Resolve(ApplicationCatalogSnapshot snapshot, ApplicationReference identity)
    {
        if (snapshot.InstanceId != identity.InstanceId || snapshot.UserId != identity.UserId)
            throw new DebugException("stale_reference", "应用引用属于其他实例或用户。", "resolving_application");
        var app = snapshot.Entries.SingleOrDefault(app => app.Package == identity.Package);
        if (app is null) throw new DebugException("app_not_found", "应用已卸载或当前用户不可见。", "resolving_application");
        if (app.InstallationRevision != identity.InstallationRevision)
            throw new DebugException("stale_reference", "应用已更新或重新安装，请刷新应用引用。", "resolving_application");
        return app;
    }
}
