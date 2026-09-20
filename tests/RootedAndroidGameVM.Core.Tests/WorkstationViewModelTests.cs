using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Ui.Workstation;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class WorkstationViewModelTests
{
    [Fact]
    public async Task Application_search_uses_real_names_and_packages_and_clears_the_previous_target()
    {
        using var model = new WorkstationViewModel(new FakeApi { Running = true });
        await model.RefreshAsync(); await model.RefreshApplicationsAsync();
        model.SelectedApplication = model.Applications.Single(app => app.Name == "Notes");
        model.ApplicationFilter = "reader";
        Assert.Equal("Reader", Assert.Single(model.FilteredApplications).Name);
        Assert.Empty(model.Package);
        model.ApplicationFilter = "test.notes";
        Assert.Equal("Notes", Assert.Single(model.FilteredApplications).Name);
        Assert.False(model.LaunchCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delayed_file_list_cannot_populate_a_different_application()
    {
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi { Running = true, FileRead = pending };
        using var model = new WorkstationViewModel(api) { Package = "test.notes" };
        await model.RefreshAsync();
        var reading = model.BrowseFilesAsync();
        model.SelectedApplication = new("test.reader", "Reader");
        pending.SetResult(JsonSerializer.SerializeToElement(new { entries = new[] { new { name = "old-app-file", details = "regular file|7|1001|1001|600" } } }));
        await reading;
        Assert.Equal("test.notes", api.Calls.Single(call => call.Command == "files.list").Text("package"));
        Assert.Empty(model.Files);
        Assert.Null(model.SelectedFile);
    }

    [Fact]
    public async Task Application_selection_is_explicit_and_shared_files_do_not_need_one()
    {
        var api = new FakeApi { Running = true };
        using var model = new WorkstationViewModel(api);
        await model.RefreshAsync();
        await model.RefreshApplicationsAsync();
        Assert.Null(model.SelectedApplication);
        Assert.Empty(model.Package);
        Assert.False(model.LaunchCommand.CanExecute(null));
        Assert.False(model.LogsCommand.CanExecute(null));
        await model.BrowseFilesAsync();
        Assert.DoesNotContain(api.Calls, call => call.Command == "files.list");

        model.Scope = model.Scopes.Single(scope => scope.Value == "shared");
        await model.BrowseFilesAsync();
        Assert.Empty(api.Calls.Last().Text("package"));
        Assert.Equal("shared", api.Calls.Last().Text("scope"));

        model.SelectedApplication = model.Applications.Single(app => app.Package == "test.reader");
        model.Scope = model.Scopes.Single(scope => scope.Value == "private");
        await model.BrowseFilesAsync();
        Assert.Equal("test.reader", api.Calls.Last().Text("package"));
        Assert.True(model.LaunchCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_keeps_the_selected_application_but_uninstall_and_session_change_clear_it()
    {
        var api = new FakeApi { Running = true };
        using var model = new WorkstationViewModel(api);
        await model.RefreshAsync(); await model.RefreshApplicationsAsync();
        model.SelectedApplication = model.Applications.Single(app => app.Package == "test.reader");
        model.RemoteFolder = "files/documents";
        await model.RefreshApplicationsAsync();
        Assert.Equal("test.reader", model.Package);
        Assert.Equal("files/documents", model.RemoteFolder);

        api.Packages = ["test.notes"];
        await model.RefreshApplicationsAsync();
        Assert.Null(model.SelectedApplication); Assert.Empty(model.Package); Assert.Empty(model.RemoteFolder);
        model.SelectedApplication = model.Applications.Single();
        api.Session = "other-session";
        await model.RefreshAsync();
        Assert.Null(model.SelectedApplication); Assert.Empty(model.Package); Assert.Empty(model.Files);
    }

    [Fact]
    public async Task Gui_uses_the_shared_session_text_and_does_not_claim_interactivity_from_activity_readiness()
    {
        var api = new FakeApi { Running = true };
        using var model = new WorkstationViewModel(api);
        await model.RefreshAsync();
        Assert.Equal("shared session evidence", model.SessionText);
        Assert.Contains(api.Calls, call => call.Command == "session.summary");
        await model.RunAsync("观察应用", DebugRequest.Create("app.observe", new { package = "test.app" }));
        Assert.Contains("Activity就绪", model.SelectedWork!.Status);
        Assert.Contains("页面可操作性仍待核验", model.Message);
    }
    [Fact]
    public async Task Task_details_keep_their_own_failure_after_a_later_success()
    {
        using var model = new WorkstationViewModel(new FakeApi { Failure = "install" });
        await model.RunAsync("安装失败", new("install"));
        var failed = model.SelectedWork!;
        await model.RunAsync("另一个任务", new("status"));
        Assert.Contains("injected install failure", failed.Details);
        Assert.DoesNotContain("injected", model.SelectedWork!.Details);
    }

    [Fact]
    public async Task Failed_start_does_not_try_to_focus_an_unavailable_window()
    {
        var api = new FakeApi { Failure = "start" };
        using var model = new WorkstationViewModel(api);
        await model.OpenAndroidCommand.ExecuteAsync();
        Assert.Equal(["start"], api.Calls.Select(call => call.Command));
        Assert.Contains("injected", model.Error);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Failed_install_remains_visible_and_does_not_get_hidden_by_refresh()
    {
        var file = Path.GetTempFileName();
        try
        {
            var api = new FakeApi { Running = true, Failure = "install" };
            using var model = new WorkstationViewModel(api) { ApkPath = file };
            await model.RefreshAsync();
            await model.InstallCommand.ExecuteAsync();
            Assert.Contains("install", model.Error);
            Assert.DoesNotContain(api.Calls, call => call.Command == "apps.list");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task File_navigation_keeps_directory_and_file_selection_distinct()
    {
        var api = new FakeApi { Running = true };
        using var model = new WorkstationViewModel(api) { Package = "test.app", RemoteFolder = "files" };
        await model.RefreshAsync(); await model.BrowseFilesAsync();
        model.SelectedFile = model.Files.Single(file => file.Name == "a.txt");
        await model.EnterSelectedFolderAsync();
        Assert.Equal("files", model.RemoteFolder);
        model.SelectedFile = model.Files.Single(file => file.IsDirectory);
        await model.EnterSelectedFolderAsync();
        Assert.Equal("files/child", model.RemoteFolder);
        Assert.Equal("files/child", api.Calls.Last().Text("remote"));
    }

    [Fact]
    public async Task Long_log_collection_does_not_disable_capture_and_can_be_cancelled()
    {
        var api = new FakeApi { Running = true };
        using var model = new WorkstationViewModel(api) { Package = "test.app" };
        await model.RefreshAsync();
        var collecting = model.LogsCommand.ExecuteAsync();
        Assert.False(model.IsBusy);
        Assert.True(model.CaptureCommand.CanExecute(null));
        Assert.Equal("log-job", model.SelectedWork?.Id);
        await model.CancelCommand.ExecuteAsync();
        await collecting;
        Assert.Contains(api.Calls, call => call.Command == "cancel" && call.Text("id") == "log-job");
    }

    [Fact]
    public async Task Rejected_runtime_configuration_leaves_the_machine_stopped_with_error_visible()
    {
        var api = new FakeApi { Running = true, Failure = "runtime.configure" };
        using var model = new WorkstationViewModel(api);
        await model.RefreshAsync();
        await model.ApplyProfileAsync();
        Assert.False(api.Running);
        Assert.DoesNotContain(api.Calls, call => call.Command == "start");
        Assert.Contains("runtime.configure", model.Error);
    }

    [Fact]
    public async Task Gui_roundtrip_preserves_custom_memory_admission()
    {
        var api = new FakeApi();
        using var model = new WorkstationViewModel(api);
        await model.RefreshRuntimeAsync();
        Assert.Equal(4096, model.StartAvailableMb);
        await model.ApplyProfileAsync();
        var profile = api.Calls.Single(c => c.Command == "runtime.configure").Value<RuntimeProfile>("profile")!;
        Assert.Equal(4096, profile.StartAvailableMb);
        Assert.Equal(3072, profile.MemoryMb);
        Assert.True(profile.LowRam);
        Assert.Equal(512, profile.VmHeapMb);
    }
    private sealed class FakeApi : IWorkstationApi
    {
        public bool Running { get; set; }
        public string Session { get; set; } = "fixture-session";
        public string[] Packages { get; set; } = ["test.notes", "test.reader"];
        public TaskCompletionSource<JsonElement>? FileRead { get; init; }
        public string? Failure { get; init; }
        public List<DebugRequest> Calls { get; } = [];
        private readonly TaskCompletionSource<JsonElement> _logs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewFrame> PreviewAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JsonElement> ExecuteAsync(DebugRequest request, CancellationToken cancellationToken, Action<JsonElement>? progress = null)
        {
            Calls.Add(request);
            if (request.Command == Failure) throw new InvalidOperationException("injected " + request.Command + " failure");
            switch (request.Command)
            {
                case "session.summary":
                    var summary = new SessionSummary(DateTimeOffset.UtcNow, "emulator-5554", Running ? "Running" : "Stopped", Running ? Session : null,
                        request.Text("package"), null, null, [], null, null, [], "fixture-artifacts", [], "observe", null, "shared session evidence");
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                        runtime = new
                        {
                            status = summary.Status,
                            serial = summary.Instance,
                            state = new { awake = true, locked = false, foreground = "test.app" }
                        },
                        summary
                    }, DebugJson.Options));
                case "app.observe": return Task.FromResult(JsonSerializer.SerializeToElement(new { stage = "activity_ready", interactiveReady = false }));
                case "apps.list": return Task.FromResult(JsonSerializer.SerializeToElement(new { entries = Packages.Select(package => new { package, name = package == "test.notes" ? "Notes" : "Reader", appRef = "ref-" + package, userId = 0, system = false, runningPids = Array.Empty<int>() }).ToArray() }));
                case "runtime.inspect": return Task.FromResult(JsonSerializer.SerializeToElement(new { requested = RuntimeProfile.Recommended with { StartAvailableMb = 4096, LowRam = true, VmHeapMb = 512 }, host = new { totalMb = 16111, availableMb = 6000 } }, DebugJson.Options));
                case "status": return Task.FromResult(JsonSerializer.SerializeToElement(new { status = Running ? "Running" : "Stopped", serial = "emulator-5554", state = new { awake = true, locked = false, foreground = "test.app" } }));
                case "stop": Running = false; break;
                case "start": Running = true; break;
                case "files.list": return FileRead?.Task ?? Task.FromResult(JsonSerializer.SerializeToElement(new { entries = new[] { new { name = "a.txt", details = "regular file|7|1001|1001|600" }, new { name = "child", details = "directory|4096|1001|1001|700" } } }));
                case "logs": progress?.Invoke(JsonSerializer.SerializeToElement(new { jobId = "log-job", status = "running" })); return _logs.Task;
                case "cancel": _logs.TrySetResult(JsonSerializer.SerializeToElement(new { cancelled = true })); break;
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { success = true }));
        }
    }
}
