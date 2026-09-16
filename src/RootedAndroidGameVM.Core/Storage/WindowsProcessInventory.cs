using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RootedAndroidGameVM.Core.Storage;

[SupportedOSPlatform("windows")]
internal static class WindowsProcessInventory
{
    private static readonly QueryProcessInformation? QueryCommandLine = LoadQuery();

    public static IReadOnlyList<HostProcessIdentity> Read(HashSet<string> paths, string[] executableNames)
    {
        if (QueryCommandLine is null) throw new PlatformNotSupportedException("进程命令行查询不可用。");
        var names = executableNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var result = new List<HostProcessIdentity>();
        if (!Process32FirstW(snapshot, ref entry))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error);
            return result;
        }
        do
        {
            if (!names.Contains(entry.Name)) continue;
            using var process = OpenProcess(0x1000, false, entry.Pid); // Query only; never read/write guest memory.
            if (process.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 87) continue; // Exited between enumeration and opening.
                throw new Win32Exception(error, "无法确认运行时进程归属。");
            }
            var image = new StringBuilder(512);
            while (true)
            {
                var size = image.Capacity;
                if (QueryFullProcessImageNameW(process, 0, image, ref size)) break;
                var error = Marshal.GetLastWin32Error();
                if (error != 122 || image.Capacity >= 32768) throw new Win32Exception(error);
                image.Capacity *= 2;
            }
            var path = Path.GetFullPath(image.ToString());
            if (!paths.Contains(path)) continue;
            if (!GetProcessTimes(process, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var commandLine = ReadCommandLine(process);
            if (!GetExitCodeProcess(process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (code != 259) continue;
            var arguments = WindowsEmulatorProcessCatalog.SplitCommandLine(commandLine);
            var directory = WindowsEmulatorProcessCatalog.ReadArgument(arguments, "-datadir");
            directory = directory is not null && Path.IsPathFullyQualified(directory)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) : null;
            result.Add(new(checked((int)entry.Pid), checked((int)entry.ParentPid), path,
                DateTime.FromFileTimeUtc(created).Ticks, WindowsEmulatorProcessCatalog.ReadArgument(arguments, "-avd"), directory,
                int.TryParse(WindowsEmulatorProcessCatalog.ReadArgument(arguments, "-port"), out var port) ? port : null));
        } while (Process32NextW(snapshot, ref entry));
        var lastError = Marshal.GetLastWin32Error();
        if (lastError != 18) throw new Win32Exception(lastError);
        return result;
    }

    private static string ReadCommandLine(SafeProcessHandle process)
    {
        const int capacity = 128 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var status = QueryCommandLine!(process, 60, buffer, capacity, out var returned);
            if (status is unchecked((int)0xc0000003) or unchecked((int)0xc0000002))
                throw new PlatformNotSupportedException("系统不支持进程命令行信息类。");
            if (status < 0) throw new IOException($"无法确认进程启动参数，NTSTATUS 0x{status:X8}。");
            if (returned < Marshal.SizeOf<UnicodeString>() || returned > capacity) throw new IOException("命令行响应长度无效。");
            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            var offset = text.Buffer.ToInt64() - buffer.ToInt64();
            if ((text.Length & 1) != 0 || text.Length > text.MaximumLength || offset < Marshal.SizeOf<UnicodeString>() || offset > returned - text.Length)
                throw new IOException("命令行响应范围无效。");
            return Marshal.PtrToStringUni(text.Buffer, text.Length / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static QueryProcessInformation? LoadQuery()
    {
        if (!NativeLibrary.TryLoad("ntdll.dll", typeof(WindowsProcessInventory).Assembly, DllImportSearchPath.System32, out var library)) return null;
        if (NativeLibrary.TryGetExport(library, "NtQueryInformationProcess", out var function))
            return Marshal.GetDelegateForFunctionPointer<QueryProcessInformation>(function); // Keep loaded for delegate lifetime.
        NativeLibrary.Free(library); return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueryProcessInformation(SafeProcessHandle process, int informationClass, IntPtr buffer, int length, out int returned);
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
