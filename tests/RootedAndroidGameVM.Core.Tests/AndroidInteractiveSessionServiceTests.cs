using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class AndroidInteractiveSessionServiceTests
{
    [Fact]
    public async Task Interactive_session_wakes_and_unlocks_after_any_android_boot()
    {
        var runner = new RecordingRunner();
        var layout = AndroidSdkLayout.FromRoot(@"D:\Product\Sdk");
        var options = AndroidVmOptions.Default;

        await new AndroidInteractiveSessionService(layout, options, runner).PrepareAsync();

        Assert.Equal(2, runner.Commands.Count);
        Assert.Contains("KEYCODE_WAKEUP", runner.Commands[0].Arguments);
        Assert.Contains("dismiss-keyguard", runner.Commands[1].Arguments);
    }

    [Fact]
    public void Magisk_open_failure_keeps_the_last_ui_diagnostic_in_the_top_level_message()
    {
        var message = MagiskPolicyAutomator.FormatOpenFailure(
            new TimeoutException("最后可见标签：Welcome | Continue"));

        Assert.Contains("Welcome | Continue", message, StringComparison.Ordinal);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessSpec> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(
            ProcessSpec spec,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(spec);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
