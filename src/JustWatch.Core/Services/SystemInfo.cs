using System.Runtime.InteropServices;
using JustWatch.Core.Models;

namespace JustWatch.Core.Services;

/// <summary>
/// Host machine resource probes (Windows). Used by the emulator dashboard to show
/// memory headroom and to bound RAM/CPU tuning to what the machine actually has.
/// </summary>
public static class SystemInfo
{
    public static AndroidHostInfo GetHostInfo()
    {
        var (total, available) = GetPhysicalMemoryMb();
        return new AndroidHostInfo
        {
            LogicalCores = Environment.ProcessorCount,
            TotalRamMb = total,
            AvailableRamMb = available
        };
    }

    private static (long TotalMb, long AvailableMb) GetPhysicalMemoryMb()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(status))
                return ((long)(status.ullTotalPhys / (1024 * 1024)),
                        (long)(status.ullAvailPhys / (1024 * 1024)));
        }
        catch { }

        return (0, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}
