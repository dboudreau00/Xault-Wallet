using System.Diagnostics;
using System.Runtime.InteropServices;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>Enhanced Security's building blocks: the input rule and the memory protection.</summary>
public class EnhancedSecurityTests
{
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_POINTERDOWN = 0x0246;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_MOUSEMOVE = 0x0200;

    [Theory]
    [InlineData(WM_LBUTTONDOWN)]
    [InlineData(WM_KEYDOWN)]
    [InlineData(WM_POINTERDOWN)]
    public void Device_And_System_Input_Operate_The_Window(uint message)
    {
        Assert.True(InputTrust.IsTrusted(message, sentByAnotherThread: false, InputTrust.OriginHardware));
        Assert.True(InputTrust.IsTrusted(message, sentByAnotherThread: false, InputTrust.OriginSystem));
    }

    [Theory]
    [InlineData(WM_LBUTTONDOWN)]
    [InlineData(WM_KEYDOWN)]
    [InlineData(WM_POINTERDOWN)]
    public void Injected_Posted_Or_Sent_Input_Does_Not(uint message)
    {
        Assert.False(InputTrust.IsTrusted(message, sentByAnotherThread: false, InputTrust.OriginInjected));   // SendInput
        Assert.False(InputTrust.IsTrusted(message, sentByAnotherThread: false, InputTrust.OriginUnavailable)); // PostMessage
        Assert.False(InputTrust.IsTrusted(message, sentByAnotherThread: true, InputTrust.OriginHardware));    // SendMessage
        Assert.False(InputTrust.IsTrusted(message, sentByAnotherThread: false, origin: null));                // couldn't tell
    }

    [Theory]
    [InlineData(WM_CHAR)]
    [InlineData(WM_MOUSEMOVE)]
    [InlineData(0x000Fu)] // WM_PAINT
    public void Messages_That_Do_Not_Act_Are_Not_Judged(uint message)
    {
        Assert.False(InputTrust.IsAction(message));
        Assert.True(InputTrust.IsTrusted(message, sentByAnotherThread: true, InputTrust.OriginInjected));
    }

    [Fact]
    public void A_Protected_Process_Can_Be_Ended_But_Not_Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(ProcessHardening.IsSupported);
            return;
        }

        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using Process child = Process.Start(psi)!;
        try
        {
            // Before: this test process (same user) may read it.
            IntPtr before = OpenProcess(PROCESS_VM_READ, false, child.Id);
            Assert.NotEqual(IntPtr.Zero, before);
            CloseHandle(before);

            Assert.True(ProcessHardening.Protect(child.Handle), "SetKernelObjectSecurity failed: " + Marshal.GetLastWin32Error());
            if (DebugPrivilegeEnabled())
            {
                return; // the debug privilege opens any process whatever its access list: nothing to observe here
            }

            foreach (uint denied in new[] { PROCESS_VM_READ, PROCESS_VM_WRITE, PROCESS_VM_OPERATION, PROCESS_CREATE_THREAD, PROCESS_DUP_HANDLE, WRITE_DAC, PROCESS_SUSPEND_RESUME })
            {
                IntPtr h = OpenProcess(denied, false, child.Id);
                int error = Marshal.GetLastWin32Error();
                if (h != IntPtr.Zero)
                {
                    CloseHandle(h);
                }

                Assert.True(h == IntPtr.Zero, $"access 0x{denied:x} was still granted");
                Assert.Equal(5, error); // ERROR_ACCESS_DENIED
            }

            IntPtr query = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, child.Id);
            Assert.NotEqual(IntPtr.Zero, query);
            CloseHandle(query);

            IntPtr end = OpenProcess(PROCESS_TERMINATE, false, child.Id);
            Assert.NotEqual(IntPtr.Zero, end);
            Assert.True(TerminateProcess(end, 0));
            CloseHandle(end);
            Assert.True(child.WaitForExit(10_000));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }

    /// <summary>Whether this process holds SeDebugPrivilege enabled (some elevated or service contexts do).
    /// PrivilegeCheck wants an impersonation token, so the process token is duplicated as one first.</summary>
    private static bool DebugPrivilegeEnabled()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, out IntPtr token))
        {
            return false;
        }

        IntPtr impersonation = IntPtr.Zero;
        try
        {
            if (!DuplicateToken(token, SecurityImpersonation, out impersonation) || !LookupPrivilegeValueW(null, "SeDebugPrivilege", out long luid))
            {
                return false;
            }

            var set = new PRIVILEGE_SET { PrivilegeCount = 1, Control = 1, Luid = luid, Attributes = 0 };
            return PrivilegeCheck(impersonation, ref set, out bool enabled) && enabled;
        }
        finally
        {
            if (impersonation != IntPtr.Zero)
            {
                CloseHandle(impersonation);
            }

            CloseHandle(token);
        }
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const int SecurityImpersonation = 2;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateToken(IntPtr token, int impersonationLevel, out IntPtr duplicate);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PRIVILEGE_SET
    {
        public uint PrivilegeCount;
        public uint Control;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrivilegeCheck(IntPtr token, ref PRIVILEGE_SET privileges, [MarshalAs(UnmanagedType.Bool)] out bool result);

    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint WRITE_DAC = 0x00040000;
    private const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
