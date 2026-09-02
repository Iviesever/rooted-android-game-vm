using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class UiStateContractTests
{
    [Theory]
    [InlineData(VmStatus.NotInstalled, "尚未安装", "开始安装")]
    [InlineData(VmStatus.Stopped, "已停止", "启动虚拟机")]
    [InlineData(VmStatus.Running, "运行中", "停止虚拟机")]
    public void Launcher_state_maps_vm_status_to_clear_primary_action(
        VmStatus status,
        string expectedTitle,
        string expectedAction)
    {
        var state = LauncherDashboardState.From(status);

        Assert.Equal(expectedTitle, state.StatusTitle);
        Assert.Equal(expectedAction, state.PrimaryActionText);
    }

    [Fact]
    public void Launcher_exposes_all_approved_top_level_actions()
    {
        var actions = LauncherActionCatalog.All;

        Assert.Equal(6, actions.Count);
        Assert.Equal(
            [LauncherAction.StartOrStop, LauncherAction.InstallApk, LauncherAction.AppsAndData,
             LauncherAction.Settings, LauncherAction.Diagnostics, LauncherAction.Repair],
            actions.Select(action => action.Action));
    }

    [Fact]
    public void Setup_stages_are_ordered_and_have_monotonic_progress()
    {
        var states = SetupProgressCatalog.All;

        Assert.Equal(
            [SetupStage.Preflight, SetupStage.Download, SetupStage.CreateAvd,
             SetupStage.Root, SetupStage.Verify, SetupStage.Complete],
            states.Select(state => state.Stage));
        Assert.Equal(100, states[^1].Percent);
        Assert.True(states.Zip(states.Skip(1), (left, right) => left.Percent < right.Percent).All(value => value));
    }
}
