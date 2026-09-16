using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RootedAndroidGameVM.Core.Debugging;

[SupportedOSPlatform("windows")]
public static class OwnedVmWindow
{
    public static object Show(OwnedInstance instance)
    {
        var identity = instance.Require(force: true);
        using var process = Process.GetProcessById(identity.ProcessId);
        var window = process.MainWindowHandle;
        if (window == 0) throw new DebugException("window_unavailable", "当前实例没有可打开的窗口，请停止后以普通模式启动。");
        GetWindowThreadProcessId(window, out var owner);
        if (owner != identity.ProcessId) throw new DebugException("instance_mismatch", "窗口已不属于产品进程。");
        ShowWindow(window, 9);
        SetForegroundWindow(window);
        return new { visible = IsWindowVisible(window), focused = GetForegroundWindow() == window };
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
