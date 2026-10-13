using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XaultWallet.Core.Companion;

/// <summary>
/// Starts XaultWallet from the native host so that it outlives the host. A browser ends its native
/// host when the message is answered (Firefox on Windows by closing the job object it put the host
/// in), so the app is started outside that job, with none of the host's handles: its standard streams
/// are the browser's message pipes and must not be held open by the app.
/// </summary>
public static class DetachedLauncher
{
    /// <summary>Start <paramref name="executable"/> with no arguments. False when it couldn't be started.</summary>
    public static bool Launch(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            return false;
        }

        try
        {
            return OperatingSystem.IsWindows() ? LaunchWindows(executable) : LaunchUnix(executable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool LaunchWindows(string executable)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        string commandLine = "\"" + executable + "\"";
        string? directory = Path.GetDirectoryName(executable);

        // Out of the browser's job when that job allows it; otherwise (no job, or no breakaway allowed)
        // a plain start, which is all that is possible then.
        foreach (uint flags in CreationFlagsToTry)
        {
            if (CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: false, flags, IntPtr.Zero, directory, ref si, out PROCESS_INFORMATION pi))
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return true;
            }
        }

        return false;
    }

    private static bool LaunchUnix(string executable)
    {
        // The shell detaches it (a new session where setsid exists) with its streams on /dev/null,
        // then exits: the app never sees the host's pipes.
        string setsid = SetsidPaths.FirstOrDefault(File.Exists) ?? string.Empty;
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("\"$@\" </dev/null >/dev/null 2>&1 &");
        psi.ArgumentList.Add("xaultwallet-launch");
        if (setsid.Length > 0)
        {
            psi.ArgumentList.Add(setsid);
        }

        psi.ArgumentList.Add(executable);
        using Process? shell = Process.Start(psi);
        if (shell is null)
        {
            return false;
        }

        shell.StandardInput.Close();
        return shell.WaitForExit(5000) && shell.ExitCode == 0;
    }

    private static readonly string[] SetsidPaths = ["/usr/bin/setsid", "/bin/setsid"];

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    /// <summary>Out of the browser's job first; then, where that job forbids it, a plain start.</summary>
    private static readonly uint[] CreationFlagsToTry =
    [
        CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT,
        CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT,
    ];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Lets the app take the foreground when this process has the right to (it was started
    /// by the browser the user just clicked in). Windows only; a no-op elsewhere or when it can't.</summary>
    public static void AllowAnyForeground()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                AllowSetForegroundWindow(ASFW_ANY);
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
