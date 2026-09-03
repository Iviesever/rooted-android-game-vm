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

    [Fact]
    public async Task Magisk_actions_reuse_the_snapshot_that_selected_each_state()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = await File.ReadAllTextAsync(Path.Combine(
            projectRoot,
            "src",
            "RootedAndroidGameVM.Core",
            "Android",
            "MagiskPolicyAutomator.cs"));

        Assert.Contains("PrepareMagiskHomeAsync", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.FindCenter(\"OK\")", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.FindCenter(\"Allow\")", source, StringComparison.Ordinal);
        Assert.Contains("actionCount <= maxSetupActions", source, StringComparison.Ordinal);
        Assert.Contains("if (actionCount == maxSetupActions)", source, StringComparison.Ordinal);
        Assert.Contains("beforePolicy.FindCenter(\"Superuser\")", source, StringComparison.Ordinal);
        Assert.Contains(
            "policySnapshot.FindCenterByResourceId(PolicyIndicatorResourceId)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if (setupRestarted)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForLabelAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitForResourceAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Magisk_home_state_requires_an_actionable_control_in_the_same_snapshot()
    {
        const string partialXml = """
            <hierarchy>
              <node text="Allow Magisk to send you notifications?" enabled="true" bounds="[0,0][100,40]" />
              <node text="Allow" enabled="false" bounds="[50,40][100,80]" />
              <node text="Superuser" enabled="true" bounds="[0,80][100,120]" />
            </hierarchy>
            """;
        const string additionalSetupPartialXml = """
            <hierarchy>
              <node text="Requires additional setup" enabled="true" bounds="[0,0][100,40]" />
              <node text="OK" enabled="false" bounds="[50,40][100,80]" />
              <node text="Allow Magisk to send you notifications?" enabled="true" bounds="[0,80][100,120]" />
              <node text="Allow" enabled="true" bounds="[50,120][100,160]" />
            </hierarchy>
            """;
        const string actionableXml = """
            <hierarchy>
              <node text="Allow Magisk to send you notifications?" enabled="true" bounds="[0,0][100,40]" />
              <node text="Allow" enabled="true" bounds="[50,40][100,80]" />
            </hierarchy>
            """;

        Assert.False(InvokeSnapshotPredicate(
            "IsActionableMagiskHomeSnapshot",
            AndroidUiSnapshot.Parse(partialXml)));
        Assert.False(InvokeSnapshotPredicate(
            "IsActionableMagiskHomeSnapshot",
            AndroidUiSnapshot.Parse(additionalSetupPartialXml)));
        Assert.True(InvokeSnapshotPredicate(
            "IsActionableMagiskHomeSnapshot",
            AndroidUiSnapshot.Parse(actionableXml)));
    }

    [Fact]
    public void Magisk_policy_state_requires_an_enabled_indicator_with_valid_bounds()
    {
        const string partialXml = """
            <hierarchy>
              <node text="[SharedUID] Shell" enabled="true" bounds="[0,0][100,40]" />
              <node resource-id="com.topjohnwu.magisk:id/policy_indicator" enabled="false" bounds="[0,40][100,80]" />
            </hierarchy>
            """;
        const string invalidBoundsXml = """
            <hierarchy>
              <node text="[SharedUID] Shell" enabled="true" bounds="[0,0][100,40]" />
              <node resource-id="com.topjohnwu.magisk:id/policy_indicator" enabled="true" bounds="invalid" />
            </hierarchy>
            """;
        const string completeXml = """
            <hierarchy>
              <node text="[SharedUID] Shell" enabled="true" bounds="[0,0][100,40]" />
              <node resource-id="com.topjohnwu.magisk:id/policy_indicator" enabled="true" bounds="[0,40][100,80]" />
            </hierarchy>
            """;
        const string zeroAreaXml = """
            <hierarchy>
              <node text="[SharedUID] Shell" enabled="true" bounds="[0,0][100,40]" />
              <node resource-id="com.topjohnwu.magisk:id/policy_indicator" enabled="true" bounds="[0,0][0,0]" />
            </hierarchy>
            """;

        Assert.False(InvokeSnapshotPredicate(
            "IsActionableMagiskPolicySnapshot",
            AndroidUiSnapshot.Parse(partialXml)));
        Assert.False(InvokeSnapshotPredicate(
            "IsActionableMagiskPolicySnapshot",
            AndroidUiSnapshot.Parse(invalidBoundsXml)));
        Assert.False(InvokeSnapshotPredicate(
            "IsActionableMagiskPolicySnapshot",
            AndroidUiSnapshot.Parse(zeroAreaXml)));
        Assert.True(InvokeSnapshotPredicate(
            "IsActionableMagiskPolicySnapshot",
            AndroidUiSnapshot.Parse(completeXml)));
    }

    private static bool InvokeSnapshotPredicate(string methodName, AndroidUiSnapshot snapshot)
    {
        var method = typeof(MagiskPolicyAutomator).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(null, new object[] { snapshot }));
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
