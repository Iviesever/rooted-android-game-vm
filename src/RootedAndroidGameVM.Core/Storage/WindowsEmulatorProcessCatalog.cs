using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace RootedAndroidGameVM.Core.Storage;

[SupportedOSPlatform("windows")]
public sealed class WindowsEmulatorProcessCatalog : IEmulatorProcessCatalog
{
    public IReadOnlyList<HostProcessIdentity> FindByExecutable(string executablePath)
    {
        var name = Path.GetFileName(executablePath);
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]+\.exe\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("运行时进程名称无效。", nameof(executablePath));
        dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
        object? connectionObject = null;
        object? rowsObject = null;
        try
        {
            dynamic connection = locator.ConnectServer(".", @"root\cimv2");
            connectionObject = connection;
            dynamic rows = connection.ExecQuery(
                $"SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine, CreationDate FROM Win32_Process WHERE Name='{name}'");
            rowsObject = rows;
            var result = new List<HostProcessIdentity>();
            foreach (var item in rows)
            {
                try
                {
                    object row = item;
                    string? path = (string?)ReadProperty(row, "ExecutablePath");
                    if (path is null) throw new IOException("无法读取模拟器进程归属，请关闭相关运行时后重试。");
                    if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase)) continue;
                    string? commandLine = (string?)ReadProperty(row, "CommandLine");
                    if (commandLine is null) throw new IOException("无法确认运行时启动参数。");
                    var args = SplitCommandLine(commandLine);
                    var avdDirectory = ReadArgument(args, "-datadir");
                    if (avdDirectory is not null && Path.IsPathFullyQualified(avdDirectory))
                        avdDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(avdDirectory));
                    else avdDirectory = null;
                    result.Add(new HostProcessIdentity(Convert.ToInt32(ReadProperty(row, "ProcessId")), Convert.ToInt32(ReadProperty(row, "ParentProcessId")), path,
                        ReadCreationTicks((string)ReadProperty(row, "CreationDate")!), ReadArgument(args, "-avd"), avdDirectory,
                        int.TryParse(ReadArgument(args, "-port"), out var port) ? port : null));
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            return result;
        }
        finally
        {
            if (rowsObject is not null) Marshal.FinalReleaseComObject(rowsObject);
            if (connectionObject is not null) Marshal.FinalReleaseComObject(connectionObject);
            Marshal.FinalReleaseComObject(locator);
        }
    }

    private static object? ReadProperty(object row, string name)
    {
        // SWbemObject's dynamic field DISPIDs vary between instances. The C# dynamic
        // binder caches them, so use the stable SWbemPropertySet interface instead.
        dynamic properties = ((dynamic)row).Properties_;
        object? property = null;
        try
        {
            property = properties.Item(name);
            return ((dynamic)property).Value;
        }
        finally
        {
            if (property is not null) Marshal.FinalReleaseComObject(property);
            Marshal.FinalReleaseComObject(properties);
        }
    }

    public IReadOnlySet<int> GetListenerOwners(int port)
    {
        var result = new HashSet<int>();
        ReadListeners(2, port, result); // IPv4.
        ReadListeners(23, port, result); // IPv6; include wildcard binds and conflicting owners.
        return result;
    }

    public async Task WaitForExitAsync(HostProcessIdentity identity, CancellationToken cancellationToken)
    {
        using var process = OpenMatchingProcess(identity);
        if (process is not null) await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task TerminateAsync(HostProcessIdentity identity, CancellationToken cancellationToken)
    {
        using var process = OpenMatchingProcess(identity);
        if (process is null) return;
        process.Kill();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Process? OpenMatchingProcess(HostProcessIdentity identity)
    {
        Process process;
        try { process = Process.GetProcessById(identity.ProcessId); }
        catch (ArgumentException) { return null; }
        try
        {
            if (process.HasExited) { process.Dispose(); return null; }
            if (!string.Equals(process.MainModule?.FileName, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                process.StartTime.ToUniversalTime().Ticks / 10 != identity.StartedAtUtcTicks / 10)
                throw new IOException("进程身份已变化，已停止操作以保护其他模拟器。");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    private static long ReadCreationTicks(string value)
    {
        var date = DateTime.ParseExact(value[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture);
        var minutes = int.Parse(value[22..25], CultureInfo.InvariantCulture) * (value[21] == '-' ? -1 : 1);
        return new DateTimeOffset(date, TimeSpan.FromMinutes(minutes)).UtcTicks;
    }

    private static string? ReadArgument(string[] args, string flag)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return Enumerable.Range(0, count).Select(index =>
                Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!).ToArray();
        }
        finally { LocalFree(pointer); }
    }

    private static void ReadListeners(int family, int port, HashSet<int> owners)
    {
        var size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (result is not (0 or 122)) throw new Win32Exception((int)result);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(pointer, ref size, false, family, 3, 0);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception((int)result);
                var count = Marshal.ReadInt32(pointer);
                var rowSize = family == 2 ? 24 : 56;
                var portOffset = family == 2 ? 8 : 20;
                var pidOffset = family == 2 ? 20 : 52;
                if (count < 0 || 4L + (long)count * rowSize > size) throw new InvalidDataException("TCP 监听表尺寸无效。");
                for (var index = 0; index < count; index++)
                {
                    var row = IntPtr.Add(pointer, 4 + index * rowSize);
                    var localPort = Marshal.ReadByte(row, portOffset) * 256 + Marshal.ReadByte(row, portOffset + 1);
                    if (localPort == port) owners.Add(Marshal.ReadInt32(row, pidOffset));
                }
                return;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        throw new IOException("TCP 监听表持续变化，请稍后重试。");
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
