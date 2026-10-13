using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using XaultWallet.Core.Diagnostics;

namespace XaultWallet.Core.Security;

/// <summary>
/// Enhanced Security's memory protection (Windows): other programs running as the same user can no
/// longer open XaultWallet, or the monero-wallet-rpc it starts, to read or write their memory or start a
/// thread in them, or change these rules back. They can still see that the processes exist and end them.
/// It does not stop code injection that needs no process handle (window hooks, or the threads, which keep
/// their default access), and a program that opened a process before this ran keeps the access it got.
/// <para>
/// It replaces the process's access list with one that lets the user's own programs only query,
/// wait for and end it, and takes away the implicit rights an owner has (an "OWNER RIGHTS" entry), so
/// a program running as the user can't grant itself more. Programs running as an administrator, or
/// with the debug privilege, are not stopped by it: nothing at the user's level can stop those. The
/// same idea as VeraCrypt's memory protection.
/// </para>
/// </summary>
public static class ProcessHardening
{
    private static readonly object Gate = new();
    private static readonly List<Process> Children = new();

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>On for the rest of this run once <see cref="Enable"/> has been called.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>This process's own access list was replaced (false when Windows refused it).</summary>
    public static bool SelfProtected { get; private set; }

    /// <summary>Protect this process and every child started so far or later.</summary>
    public static void Enable()
    {
        if (!IsSupported)
        {
            return;
        }

        lock (Gate)
        {
            bool self = Protect(GetCurrentProcess());
            int error = Marshal.GetLastWin32Error();
            SelfProtected |= self;
            Enabled = true;
            if (!self)
            {
                Log.WarnOnce("hardening-self", $"Enhanced Security couldn't protect XaultWallet's memory (error {error}).");
            }

            Children.RemoveAll(HasExited);
            foreach (Process child in Children)
            {
                ProtectChild(child);
            }
        }
    }

    /// <summary>A child holding wallet secrets (monero-wallet-rpc): protected now if Enhanced Security is
    /// on, or as soon as it is turned on.</summary>
    public static void Track(Process child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!IsSupported)
        {
            return;
        }

        lock (Gate)
        {
            Children.RemoveAll(HasExited);
            Children.Add(child);
            if (Enabled)
            {
                ProtectChild(child);
            }
        }
    }

    /// <summary>Apply the protected access list to a process this app holds a full handle to.</summary>
    internal static bool Protect(IntPtr process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string? user = WindowsIdentity.GetCurrent().User?.Value;
        if (user is null)
        {
            return false;
        }

        // P: protected (no inheritance). SYSTEM keeps full access (the OS and security software). The
        // user's own programs may query (limited), wait for and end the process: 0x101001 is
        // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE. OW (OWNER RIGHTS) gets
        // READ_CONTROL only, which removes the owner's implicit right to rewrite this list.
        string sddl = "D:P(A;;0x1fffff;;;SY)(A;;0x101001;;;" + user + ")(A;;RC;;;OW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr descriptor, IntPtr.Zero))
        {
            return false;
        }

        try
        {
            return SetKernelObjectSecurity(process, DACL_SECURITY_INFORMATION, descriptor);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static void ProtectChild(Process child)
    {
        try
        {
            if (!child.HasExited && !Protect(child.Handle))
            {
                Log.WarnOnce("hardening-child", $"Enhanced Security couldn't protect the wallet backend's memory (error {Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // it exited meanwhile
        }
    }

    private static bool HasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private const uint DACL_SECURITY_INFORMATION = 0x00000004;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor, uint stringSdRevision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(IntPtr handle, uint securityInformation, IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>
/// Enhanced Security's input rule (Windows): which keyboard, mouse, touch and pen messages may operate
/// the window. Only input the system reports as coming from a device (or from the system itself, or from
/// an accessibility program Windows trusts with UIAccess, such as its on-screen keyboard) counts. Input
/// another program injected with SendInput, or posted or sent as a fake window message (how hidden-VNC
/// malware drives a window), is dropped. Typed characters (WM_CHAR) are left alone: they can fill a
/// field, but every action needs a key press or a click, which is checked here.
/// </summary>
public static class InputTrust
{
    // Window messages that act: key presses, button presses and releases, wheels, pointer contact.
    private static readonly HashSet<uint> Actions =
    [
        0x0100, 0x0101, 0x0104, 0x0105, // WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN, WM_SYSKEYUP
        0x0201, 0x0202, 0x0203,         // WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK
        0x0204, 0x0205, 0x0206,         // WM_RBUTTON*
        0x0207, 0x0208, 0x0209,         // WM_MBUTTON*
        0x020A, 0x020E,                 // WM_MOUSEWHEEL, WM_MOUSEHWHEEL
        0x020B, 0x020C, 0x020D,         // WM_XBUTTON*
        0x0240,                         // WM_TOUCH
        0x0246, 0x0247, 0x024E, 0x024F, // WM_POINTERDOWN, WM_POINTERUP, WM_POINTERWHEEL, WM_POINTERHWHEEL
        0x00A4, 0x00A5, 0x00A6,         // WM_NCRBUTTON* (the window turns non-client presses into clicks)
        0x00A7, 0x00A8, 0x00A9,         // WM_NCMBUTTON*
        0x00AB, 0x00AC, 0x00AD,         // WM_NCXBUTTON*

        // Left out on purpose: WM_NCLBUTTON* (dragging the window by its own title bar may arrive as a message
        // the window sends itself) and the pointer update and non-client pointer messages, until a check on
        // real touch and pen hardware shows they keep working.
    ];

    public const int OriginUnavailable = 0;
    public const int OriginHardware = 1;
    public const int OriginInjected = 2;
    public const int OriginSystem = 4;

    /// <summary>Whether <paramref name="message"/> is one this rule judges (the others always pass).</summary>
    public static bool IsAction(uint message) => Actions.Contains(message);

    /// <summary>Whether the message being handled came from a device or the system: not sent from another
    /// thread, and its source known to be hardware or the system. Used for any input message (a mouse
    /// move counts as the user being there only when it is real).</summary>
    public static bool IsFromDevice(bool sentByAnotherThread, int? origin) =>
        !sentByAnotherThread && origin is OriginHardware or OriginSystem;

    /// <summary>
    /// Whether an action message may reach the window. <paramref name="sentByAnotherThread"/>: the
    /// message arrived through SendMessage from another thread (InSendMessageEx). <paramref name="origin"/>:
    /// GetCurrentInputMessageSource's origin, or null when that call failed. Anything unclear is refused.
    /// </summary>
    public static bool IsTrusted(uint message, bool sentByAnotherThread, int? origin)
    {
        if (!IsAction(message))
        {
            return true;
        }

        return IsFromDevice(sentByAnotherThread, origin);
    }
}
