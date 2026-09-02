namespace RootedAndroidGameVM.Core.Ui;

public enum LauncherAction
{
    StartOrStop,
    InstallApk,
    AppsAndData,
    Settings,
    Diagnostics,
    Repair
}

public sealed record LauncherActionDescriptor(
    LauncherAction Action,
    string Title,
    string Description,
    string Glyph);

public static class LauncherActionCatalog
{
    public static IReadOnlyList<LauncherActionDescriptor> All { get; } =
    [
        new(LauncherAction.StartOrStop, "启动 / 停止", "控制安卓虚拟机", "▶"),
        new(LauncherAction.InstallApk, "安装或更新 APK", "选择本机 APK 文件", "＋"),
        new(LauncherAction.AppsAndData, "应用与数据", "启动应用、浏览或导出数据", "▦"),
        new(LauncherAction.Settings, "性能设置", "调整画质、内存与窗口", "⚙"),
        new(LauncherAction.Diagnostics, "诊断", "检查 ADB、Root 与运行环境", "✓"),
        new(LauncherAction.Repair, "修复", "修复虚拟机或重新配置 Root", "↻")
    ];
}
