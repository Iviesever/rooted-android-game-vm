namespace RootedAndroidGameVM.Core.Ui;

public enum SetupStage
{
    Preflight,
    Download,
    CreateAvd,
    Root,
    Verify,
    Complete
}

public sealed record SetupProgressState(
    SetupStage Stage,
    int Percent,
    string Title,
    string Detail);

public static class SetupProgressCatalog
{
    public static IReadOnlyList<SetupProgressState> All { get; } =
    [
        new(SetupStage.Preflight, 5, "检查电脑", "确认虚拟化、磁盘空间与系统组件。"),
        new(SetupStage.Download, 25, "下载运行环境", "获取经过校验的 Android SDK 组件。"),
        new(SetupStage.CreateAvd, 55, "创建虚拟机", "配置适合游戏的 Android 虚拟设备。"),
        new(SetupStage.Root, 75, "配置 Root", "安装并验证 Root 权限。"),
        new(SetupStage.Verify, 90, "最终验证", "检查启动、ADB 与私有数据访问。"),
        new(SetupStage.Complete, 100, "安装完成", "现在可以从桌面直接启动。")
    ];
}
