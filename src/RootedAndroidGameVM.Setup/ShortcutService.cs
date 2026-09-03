using System.IO;
using System.Runtime.InteropServices;

namespace RootedAndroidGameVM.Setup;

internal static class ShortcutService
{
    public static void CreateLauncherStartMenuShortcut(string launcherPath)
    {
        if (!File.Exists(launcherPath))
        {
            throw new FileNotFoundException("找不到日常启动器。", launcherPath);
        }

        var startMenuDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Rooted Android Game VM");
        Directory.CreateDirectory(startMenuDirectory);
        CreateShortcut(
            Path.Combine(startMenuDirectory, "Rooted Android Game VM.lnk"),
            launcherPath);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("Windows Script Host 不可用，无法创建快捷方式。");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("无法启动 Windows Script Host。");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);
            var shortcutType = shortcut?.GetType()
                ?? throw new InvalidOperationException("无法创建快捷方式对象。");
            shortcutType.InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.SetProperty,
                binder: null,
                target: shortcut,
                args: [targetPath]);
            shortcutType.InvokeMember(
                "WorkingDirectory",
                System.Reflection.BindingFlags.SetProperty,
                binder: null,
                target: shortcut,
                args: [Path.GetDirectoryName(targetPath)!]);
            shortcutType.InvokeMember(
                "Save",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shortcut,
                args: null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
