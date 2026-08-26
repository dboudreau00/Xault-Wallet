using System.Diagnostics;
using System.Runtime.InteropServices;
using XaultWallet.Core.Diagnostics;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Ties child processes to this process's lifetime on Windows via a Job Object with
/// KILL_ON_JOB_CLOSE. Without it, a crashed/killed XaultWallet leaves monero-wallet-rpc
/// running indefinitely with the REAL wallet open on an unauthenticated loopback port —
/// any local process could then drive it. The OS closes the job handle when this process
/// dies (for any reason), which kills every assigned child. No-op on other platforms.
/// </summary>
internal static class WindowsChildJob
{
    private static readonly object Gate = new();
    private static IntPtr _job; // deliberately never closed: the OS closing it at process death IS the mechanism

    /// <summary>Assign a started child to the kill-on-close job. Best-effort: failure is logged
    /// once but never blocks the launch (the explicit Kill on dispose still applies).</summary>
    public static void TryAssign(Process child)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJob();
                }

                if (_job != IntPtr.Zero && !AssignProcessToJobObject(_job, child.Handle))
                {
                    Log.Warn($"Could not assign wallet-rpc to the kill-on-close job (error {Marshal.GetLastWin32Error()}).");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Kill-on-close job assignment failed: " + ex.GetType().Name);
        }
    }

    private static IntPtr CreateJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            Log.Warn($"CreateJobObject failed (error {Marshal.GetLastWin32Error()}).");
            return IntPtr.Zero;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buf, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buf, (uint)size))
            {
                Log.Warn($"SetInformationJobObject failed (error {Marshal.GetLastWin32Error()}).");
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return job;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
