using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JustWatch.Core.Services;

/// <summary>
/// A process-wide Windows Job Object configured with KILL_ON_JOB_CLOSE. Every
/// child process we spawn (dev servers, agent CLIs, builds) is assigned to it,
/// so when THIS process exits — for any reason, including a crash or an external
/// <c>taskkill /F</c> that bypasses graceful shutdown — the OS closes the job
/// handle and terminates every assigned process. This is what guarantees dev
/// servers can never be orphaned and pile up until the machine runs out of
/// resources.
/// </summary>
public static class WindowsJobObject
{
    /// <summary>A nested job for one service. Killing a parent alone can orphan npm/yarn children.</summary>
    internal sealed class ServiceJob : IDisposable
    {
        private IntPtr _job;
        internal ServiceJob(IntPtr job) => _job = job;
        public async Task TerminateAsync()
        {
            if (_job == IntPtr.Zero) return;
            if (!TerminateJobObject(_job, 1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero) && accounting.ActiveProcesses != 0)
            {
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("The service process tree has not finished stopping.");
                await Task.Delay(25);
            }
        }
        public void Dispose() { var job = Interlocked.Exchange(ref _job, IntPtr.Zero); if (job != IntPtr.Zero) CloseHandle(job); }
    }

    internal static ServiceJob? CreateServiceJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return null;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        { BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
        var length = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length) && AssignProcessToJobObject(job, process.Handle))
                return new ServiceJob(job);
            CloseHandle(job); return null;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static readonly object Gate = new();
    private static IntPtr _handle = IntPtr.Zero;
    private static bool _initialized;

    /// <summary>
    /// Assign a started process to the kill-on-close job. Best-effort: a failure
    /// here must never prevent the process from running, so all errors are swallowed.
    /// </summary>
    public static void AssignProcess(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            EnsureCreated();
            if (_handle != IntPtr.Zero)
                AssignProcessToJobObject(_handle, process.Handle);
        }
        catch { /* best-effort cleanup wiring — never break process startup */ }
    }

    private static void EnsureCreated()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;

            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            var length = Marshal.SizeOf(info);
            var infoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, infoPtr, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
            // The handle is intentionally never closed: it must stay open for the
            // lifetime of the process so KILL_ON_JOB_CLOSE fires only when we exit.
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass,
        out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, uint length, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
