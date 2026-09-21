using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Ui.Workstation;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class FileWorkspaceTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, DebugJson.Options);
    private static readonly FileRootDescriptor Root = new("private-root", "private", "私有数据", "/data/user/10/test.notes", true, true, true, false, null, "root-entry");
    private static RemoteFileEntry Entry(string name, string kind = "file") => new(name, name, kind, 1234, 0, 1010123, 1010123, "600", "version", EntryRef: "entry-" + name);
    private static FileBrowsePage Page(string relative = "", string? cursor = null) => new(Root.RootRef, relative,
        Entry(relative, "directory") with { EntryRef = "directory-" + relative }, [Entry("documents", "directory"), Entry("中文.txt")], 3, cursor, "snapshot", DateTimeOffset.UtcNow);

    private sealed class Backend
    {
        public List<DebugRequest> Calls { get; } = [];
        public Func<DebugRequest, Action<JsonElement>?, Task<JsonElement?>?>? Override { get; set; }
        public Task<JsonElement?> Run(string title, DebugRequest request, bool exclusive, Action<JsonElement>? progress)
        {
            Calls.Add(request);
            if (Override?.Invoke(request, progress) is { } overridden) return overridden;
            object value = request.Command switch
            {
                "users.list" => new { users = new[] { new AndroidUser(0, "所有者", true), new AndroidUser(10, "测试用户", true) } },
                "apps.list" => new
                {
                    entries = new[] {
                    new { package = "test.notes", name = "笔记", appRef = "notes-" + request.Number("userId", 0), userId = request.Number("userId", 0), system = false },
                    new { package = "test.reader", name = "阅读器", appRef = "reader-" + request.Number("userId", 0), userId = request.Number("userId", 0), system = false } }
                },
                "files.roots" => new { roots = new[] { Root } },
                "files.browse" => Page(request.Text("relativePath"), request.Text("cursor").Length == 0 ? "page-2" : null),
                "files.transfer.list" => new { transfers = new[] { new FileTransferHistoryRow("original-plan", "upload", "cancelled", DateTimeOffset.UtcNow, 3, 1234, "records", true) } },
                "files.transfer.start" or "files.transfer.resume" => new { transferVerified = true },
                _ => new { success = true }
            };
            return Task.FromResult<JsonElement?>(Json(value));
        }
        public async Task<FileWorkspaceViewModel> Open()
        {
            var model = new FileWorkspaceViewModel(Run);
            model.UpdateContext(true, "1:2", "data"); await model.InitializeAsync(); return model;
        }
    }

    [Fact]
    public async Task Selecting_a_user_and_searching_apps_needs_no_typed_package_and_invalidates_old_files()
    {
        var backend = new Backend(); var model = await backend.Open();
        model.SelectedUser = model.Users.Single(user => user.UserId == 10);
        await model.RefreshApplicationsAsync();
        model.Search = "笔记"; model.SelectedApplication = Assert.Single(model.FilteredApplications);
        await model.RefreshRootsAsync();
        var request = backend.Calls.Last(call => call.Command == "files.roots");
        Assert.Equal("notes-10", request.Text("appRef")); Assert.Equal(10, request.Number("userId", 0));
        Assert.True(model.CanUpload);
        model.Search = "reader";
        Assert.Null(model.SelectedApplication); Assert.Empty(model.Entries); Assert.False(model.CanUpload);
        Assert.Equal("阅读器", Assert.Single(model.FilteredApplications).Name);
    }

    [Fact]
    public async Task Multi_selection_and_folders_use_versioned_references_and_navigation_updates_destination()
    {
        var backend = new Backend(); var model = await backend.Open();
        model.SetSelection(model.Entries.ToArray());
        var request = model.DownloadRequest(Path.GetTempPath());
        var sources = request.Arguments!["sources"].Deserialize<TransferSource[]>(DebugJson.Options)!;
        Assert.Equal(["entry-documents", "entry-中文.txt"], sources.Select(source => source.EntryRef));
        await model.OpenEntryAsync(model.Entries[0]);
        Assert.Equal("/data/user/10/test.notes/documents", model.Address); Assert.Empty(model.Selection);
        Assert.Equal("documents", model.Breadcrumbs.Last().Title);
        var upload = model.UploadRequest([Path.Combine(Path.GetTempPath(), "file.txt"), Path.Combine(Path.GetTempPath(), "folder")]);
        Assert.Equal("directory-documents", upload.Arguments!["destination"].GetProperty("entryRef").GetString());
        Assert.Equal(2, upload.Arguments["sources"].GetArrayLength());
        await model.BackAsync(); Assert.Equal(Root.DisplayPath, model.Address);
    }

    [Fact]
    public async Task Selecting_an_unavailable_root_clears_the_previous_transfer_target()
    {
        var backend = new Backend(); var model = await backend.Open();
        model.SetSelection(model.Entries.ToArray());
        var locked = Root with { RootRef = "locked", Accessible = false, Locked = true, Reason = "user_locked" };
        await model.NavigateAsync(locked, "");
        Assert.Empty(model.Entries); Assert.Empty(model.Selection);
        Assert.False(model.CanUpload); Assert.False(model.CanDownload); Assert.False(model.CanExportDirectory);
        Assert.Contains("用户未解锁", model.Message);
        Assert.Throws<InvalidOperationException>(() => model.UploadRequest([Path.GetTempPath()]));
    }

    [Fact]
    public async Task Missing_creatable_root_is_planned_without_writes_and_refreshed_after_creation()
    {
        var backend = new Backend(); var model = await backend.Open();
        var missing = Root with { Accessible = false, Exists = false, Creatable = true, Reason = "not_created", EntryRef = null };
        backend.Calls.Clear();
        await model.NavigateAsync(missing, "new-folder");
        Assert.Empty(backend.Calls); Assert.True(model.CanUpload); Assert.False(model.CanExportDirectory);
        var upload = model.UploadRequest([Path.GetTempPath()]);
        Assert.True(upload.Arguments!["createParents"].GetBoolean());
        Assert.Equal(Root.RootRef, upload.Arguments["destination"].GetProperty("rootRef").GetString());
        Assert.Equal("new-folder", upload.Arguments["destination"].GetProperty("relativePath").GetString());
        await model.RefreshDirectoryAsync();
        Assert.Equal(new[] { "files.roots", "files.browse" }, backend.Calls.Select(call => call.Command));
        Assert.Equal("new-folder", backend.Calls.Last().Text("relativePath"));
        Assert.True(model.CanExportDirectory);
        Assert.Equal("directory-new-folder", model.UploadRequest([Path.GetTempPath()]).Arguments!["destination"].GetProperty("entryRef").GetString());
    }

    [Fact]
    public async Task A_late_directory_reply_cannot_populate_a_new_session()
    {
        var backend = new Backend(); var model = await backend.Open();
        var pending = new TaskCompletionSource<JsonElement?>();
        backend.Override = (request, _) => request.Command == "files.browse" ? pending.Task : null;
        var reading = model.NavigateAsync(Root, "old-folder");
        model.UpdateContext(true, "1:3", "data"); pending.SetResult(Json(Page("old-folder"))); await reading;
        Assert.Empty(model.Entries); Assert.Empty(model.Breadcrumbs); Assert.False(model.CanUpload);
    }

    [Fact]
    public async Task Concurrent_next_page_requests_append_once_and_tree_has_its_own_continuation()
    {
        var backend = new Backend(); var model = await backend.Open();
        var pending = new TaskCompletionSource<JsonElement?>();
        backend.Override = (request, _) => request.Command == "files.browse" ? pending.Task : null;
        var reading = model.LoadMoreAsync(); await model.LoadMoreAsync();
        pending.SetResult(Json(Page() with { Entries = [Entry("last.txt")] })); await reading;
        Assert.Equal(3, model.Entries.Count); Assert.False(model.HasMore);
        backend.Override = null;
        var root = Assert.Single(model.Tree); await model.ExpandAsync(root);
        var more = Assert.Single(root.Children, node => node.IsMore); await model.OpenNodeAsync(more);
        Assert.DoesNotContain(root.Children, node => node.IsMore);
        Assert.Equal("page-2", backend.Calls.Last().Text("cursor"));
    }

    [Fact]
    public async Task Cancel_targets_the_current_transfer_and_resume_explicitly_uses_the_original_plan()
    {
        var backend = new Backend(); var model = await backend.Open();
        var pending = new TaskCompletionSource<JsonElement?>();
        backend.Override = (request, progress) =>
        {
            if (request.Command != "files.transfer.start") return null;
            progress!(Json(new { jobId = "own-job", stage = "transferring", progress = new { bytes = 1048576, totalBytes = 2097152 } })); return pending.Task;
        };
        var execution = model.ExecuteAsync("original-plan", "overwrite", true, "stable-key");
        Assert.True(model.CanCancel); Assert.Contains("1 MiB / 2 MiB", model.ProgressText);
        var callCount = backend.Calls.Count;
        await model.InitializeAsync();
        Assert.Equal(callCount, backend.Calls.Count); Assert.True(model.CanCancel); Assert.False(model.CanChangeScope);
        await model.CancelAsync(); Assert.Equal("own-job", backend.Calls.Last().Text("id"));
        pending.SetResult(Json(new { transferVerified = true })); await execution;
        Assert.Contains("已核验", model.Message); Assert.False(model.CanCancel);
        await model.ResumeAsync(Assert.Single(model.Transfers));
        var resume = backend.Calls.Last(call => call.Command == "files.transfer.resume");
        Assert.Equal("original-plan", resume.Text("planId")); Assert.NotEmpty(resume.Text("idempotencyKey"));
        Assert.False(resume.Arguments!.ContainsKey("conflictPolicy"));
    }

    [Fact]
    public async Task History_remains_readable_while_stopped_and_never_auto_replays()
    {
        var backend = new Backend(); var model = new FileWorkspaceViewModel(backend.Run);
        model.UpdateContext(false, null, "data"); await model.InitializeAsync();
        Assert.Single(model.Transfers); Assert.False(model.CanResumeSelected);
        Assert.All(backend.Calls, call => Assert.Equal("files.transfer.list", call.Command));
    }

    [Theory]
    [InlineData("completed", "已提交并核验")]
    [InlineData("committing", "提交待确认")]
    public void Results_keep_partial_failure_backup_and_pagination_visible(string itemStatus, string expected)
    {
        var row = new TransferPlanEntry(100, "source", "file.txt", "folder/file.txt", new("file", 10, "v", "sha"), null, "new");
        var page = new TransferResultPage(Json(new
        {
            summary = new { status = "failed", totalEntries = 205 },
            execution = new TransferExecutionHeader("plan", new("overwrite", true), "session", "job", "failed", DateTimeOffset.UtcNow, "设备断连"),
            executionEntries = new[] { new TransferItemState(100, itemStatus, "actual/file.txt", 10, "temporary", "original-backup", "sha", "写入回执待确认") },
            entries = new[] { row },
            nextOffset = 200
        }));
        Assert.Contains("失败", page.Summary); Assert.Contains("设备断连", page.Summary);
        var result = Assert.Single(page.Rows);
        Assert.Equal("actual/file.txt", result.Path); Assert.Equal(expected, result.Status);
        Assert.Contains("original-backup", result.Detail); Assert.Contains("temporary", result.Detail);
        Assert.Contains("写入回执待确认", result.Detail); Assert.Equal(200, page.NextOffset);
    }

    [Theory]
    [InlineData("windows_name_unsupported:folder/a|b", true, false)]
    [InlineData("insufficient_space", false, false)]
    [InlineData("parent_type_conflict:folder", false, true)]
    public void Review_separates_resolvable_conflicts_from_blocked_plans(string issue, bool archive, bool execute)
    {
        var review = new TransferReviewModel(Json(new
        {
            planId = "plan",
            direction = "download",
            totalEntries = 1,
            totalBytes = 2,
            counts = new { blocked = 1 },
            issues = new[] { issue },
            applicationsToStop = Array.Empty<TransferApplication>(),
            requiresConflictPolicy = true,
            conflicts = Array.Empty<TransferPlanEntry>(),
            preview = Array.Empty<TransferPlanEntry>()
        }), "destination");
        Assert.True(review.NeedsReview); Assert.Equal(archive, review.NeedsArchive); Assert.Equal(execute, review.CanExecute);
        Assert.Contains("2 B", review.Description);
        Assert.Equal("keep-both", review.Policy.Value);
    }
}
