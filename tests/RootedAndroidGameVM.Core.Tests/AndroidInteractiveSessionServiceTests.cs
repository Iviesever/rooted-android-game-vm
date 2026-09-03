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

    [Fact]
    public void System_ui_anr_with_a_wait_action_is_recoverable()
    {
        const string xml = """
            <hierarchy>
              <node text="System UI isn't responding" enabled="true" bounds="[0,0][100,40]" />
              <node text="Close app" enabled="true" bounds="[0,40][50,80]" />
              <node text="Wait" enabled="true" bounds="[50,40][100,80]" />
            </hierarchy>
            """;

        Assert.True(MagiskPolicyAutomator.IsRecoverableSystemAppAnrDialog(AndroidUiSnapshot.Parse(xml)));
    }

    [Fact]
    public void Digital_wellbeing_anr_with_a_wait_action_is_recoverable()
    {
        const string xml = """
            <hierarchy>
              <node text="Digital Wellbeing isn't responding" enabled="true" bounds="[0,0][100,40]" />
              <node text="Close app" enabled="true" bounds="[0,40][50,80]" />
              <node text="Wait" enabled="true" bounds="[50,40][100,80]" />
            </hierarchy>
            """;

        Assert.True(MagiskPolicyAutomator.IsRecoverableSystemAppAnrDialog(AndroidUiSnapshot.Parse(xml)));
    }

    [Fact]
    public void Unrelated_wait_dialog_is_never_auto_accepted()
    {
        const string xml = """
            <hierarchy>
              <node text="Example Game isn't responding" enabled="true" bounds="[0,0][100,40]" />
              <node text="Close app" enabled="true" bounds="[0,40][50,80]" />
              <node text="Wait" enabled="true" bounds="[50,40][100,80]" />
            </hierarchy>
            """;

        Assert.False(MagiskPolicyAutomator.IsRecoverableSystemAppAnrDialog(AndroidUiSnapshot.Parse(xml)));
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
