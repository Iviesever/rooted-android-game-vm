using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class ApplicationCatalogTests
{
    private const string Instance = "11223344556677889900aabbccddeeff";
    private static CatalogApplication App(string package, string name, int user = 0, long update = 1)
    {
        var revision = ApplicationCatalog.Revision(package, user, 10123, 1, update, 1, "/data/app/unique/base.apk");
        var reference = ApplicationCatalog.Reference(new(Instance, user, package, revision));
        return new(reference, package, name, "package_manager", user, 10123, "1.0", 1, false, true, revision, [],
            null, "/data/user/0/" + package, "/data/user/0/" + package, "/data/user_de/0/" + package, null, null);
    }
    private static ApplicationCatalogSnapshot Snapshot(params CatalogApplication[] entries) =>
        new(Instance, "vm-session", 0, "zh-CN", DateTimeOffset.UtcNow, entries, "catalog-version-1");

    [Fact]
    public void Search_by_display_name_or_package_keeps_duplicate_names_distinct_and_pages_without_loss()
    {
        var snapshot = Snapshot(App("test.notes.a", "笔记"), App("test.notes.b", "笔记"), App("test.reader", "Reader"));
        var first = ApplicationCatalog.Page(snapshot, "笔记", 1, "");
        Assert.Equal(2, first.Total);
        Assert.Equal("test.notes.a", Assert.Single(first.Entries).Package);
        var second = ApplicationCatalog.Page(snapshot, "笔记", 1, first.NextCursor!);
        Assert.Equal("test.notes.b", Assert.Single(second.Entries).Package);
        Assert.Null(second.NextCursor);
        Assert.Equal("test.reader", Assert.Single(ApplicationCatalog.Page(snapshot, "TEST.READER", 100, "").Entries).Package);
        Assert.Empty(ApplicationCatalog.Page(snapshot, "missing", 100, "").Entries);
    }

    [Fact]
    public void A_page_cursor_cannot_be_reused_for_a_changed_catalog_or_search()
    {
        var snapshot = Snapshot(App("test.one", "One"), App("test.two", "Two"));
        var first = ApplicationCatalog.Page(snapshot, "", 1, "");
        Assert.Equal("stale_cursor", Assert.Throws<DebugException>(() => ApplicationCatalog.Page(snapshot with { SnapshotId = "new" }, "", 1, first.NextCursor!)).Code);
        Assert.Equal("stale_cursor", Assert.Throws<DebugException>(() => ApplicationCatalog.Page(snapshot, "two", 1, first.NextCursor!)).Code);
        Assert.Throws<ArgumentException>(() => ApplicationCatalog.Page(snapshot, "", 201, ""));
    }

    [Fact]
    public void App_references_reject_other_instances_users_uninstalls_and_reinstalls()
    {
        var app = App("test.notes", "Notes");
        var identity = ApplicationCatalog.ParseReference(app.AppRef);
        var snapshot = Snapshot(app);
        Assert.Equal(app, ApplicationCatalog.Resolve(snapshot, identity));
        Assert.Equal("stale_reference", Assert.Throws<DebugException>(() => ApplicationCatalog.Resolve(snapshot, identity with { InstanceId = new string('a', 32) })).Code);
        Assert.Equal("stale_reference", Assert.Throws<DebugException>(() => ApplicationCatalog.Resolve(snapshot, identity with { UserId = 10 })).Code);
        Assert.Equal("app_not_found", Assert.Throws<DebugException>(() => ApplicationCatalog.Resolve(Snapshot(), identity)).Code);
        Assert.Equal("stale_reference", Assert.Throws<DebugException>(() => ApplicationCatalog.Resolve(Snapshot(App("test.notes", "Notes", update: 2)), identity)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("app1.not valid*")]
    [InlineData("app1.e30")]
    public void Malformed_references_fail_as_structured_errors(string value) =>
        Assert.Equal("invalid_app_reference", Assert.Throws<DebugException>(() => ApplicationCatalog.ParseReference(value)).Code);

    [Fact]
    public void Platform_package_and_arbitrary_ordinary_apps_have_no_game_specific_mapping()
    {
        Assert.Equal("android", AndroidPackageName.Parse("android").Value);
        Assert.Equal("test.notes", ApplicationCatalog.ParseReference(App("test.notes", "Notes").AppRef).Package);
    }

    [Fact]
    public void Embedded_helper_matches_its_manifest_and_current_source()
    {
        var assembly = typeof(ApplicationCatalog).Assembly;
        using var metadata = assembly.GetManifestResourceStream("RootedAndroidGameVM.catalog-manifest.json")!;
        using var manifest = JsonDocument.Parse(metadata);
        using var dex = assembly.GetManifestResourceStream("RootedAndroidGameVM.catalog.dex")!;
        Assert.Equal(manifest.RootElement.GetProperty("dexSha256").GetString(), Convert.ToHexString(SHA256.HashData(dex)).ToLowerInvariant());
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "RootedAndroidGameVM.sln"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var sourceRoot = Path.Combine(repo.FullName, "tools", "android-catalog", "src");
        var index = string.Concat(Directory.EnumerateFiles(sourceRoot, "*.java", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
            Path.GetRelativePath(sourceRoot, path).Replace('\\', '/') + "=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n")))).ToLowerInvariant() + "\n"));
        Assert.Equal(manifest.RootElement.GetProperty("sourceSha256").GetString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index))).ToLowerInvariant());
    }
}
