using System.Text.Json;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Ui.Workstation;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class WorkstationViewModelTests
{
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
            Assert.DoesNotContain(api.Calls, call => call.Command == "apps");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task File_navigation_keeps_directory_and_file_selection_distinct()
    {
        var api = new FakeApi { Running = true };
        using var model = new WorkstationViewModel(api);
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
        using var model = new WorkstationViewModel(api);
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

    private sealed class FakeApi : IWorkstationApi
    {
        public bool Running { get; set; }
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
                case "status": return Task.FromResult(JsonSerializer.SerializeToElement(new { status = Running ? "Running" : "Stopped", serial = "emulator-5554", state = new { awake = true, locked = false, foreground = "test.app" } }));
                case "stop": Running = false; break;
                case "start": Running = true; break;
                case "files.list": return Task.FromResult(JsonSerializer.SerializeToElement(new { entries = new[] { new { name = "a.txt", details = "regular file|7|1001|1001|600" }, new { name = "child", details = "directory|4096|1001|1001|700" } } }));
                case "logs": progress?.Invoke(JsonSerializer.SerializeToElement(new { jobId = "log-job", status = "running" })); return _logs.Task;
                case "cancel": _logs.TrySetResult(JsonSerializer.SerializeToElement(new { cancelled = true })); break;
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { success = true }));
        }
    }
}
