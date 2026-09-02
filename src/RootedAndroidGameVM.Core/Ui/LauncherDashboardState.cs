namespace RootedAndroidGameVM.Core.Ui;

public enum VmStatus
{
    NotInstalled,
    Stopped,
    Running
}

public sealed record LauncherDashboardState(
    VmStatus Status,
    string StatusTitle,
    string StatusDetail,
    string PrimaryActionText)
{
    public static LauncherDashboardState From(VmStatus status) => status switch
    {
        VmStatus.NotInstalled => new(status, "尚未安装", "完成首次安装后即可启动安卓虚拟机。", "开始安装"),
        VmStatus.Stopped => new(status, "已停止", "虚拟机已就绪，不占用运行内存。", "启动虚拟机"),
        VmStatus.Running => new(status, "运行中", "安卓虚拟机已连接，可以安装应用或访问数据。", "停止虚拟机"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
