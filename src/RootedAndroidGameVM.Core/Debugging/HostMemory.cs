using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RootedAndroidGameVM.Core.Debugging;

public sealed record HostMemorySnapshot(long TotalMb, long AvailableMb, int LogicalCores, int LoadPercent,
    long? AvailableCommitMb = null);

[SupportedOSPlatform("windows")]
public static class HostMemory
{
    public static HostMemorySnapshot Read()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new System.ComponentModel.Win32Exception();
        return new((long)(status.TotalPhysical / 1024 / 1024), (long)(status.AvailablePhysical / 1024 / 1024),
            Environment.ProcessorCount, (int)status.MemoryLoad, (long)(status.AvailablePageFile / 1024 / 1024));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
