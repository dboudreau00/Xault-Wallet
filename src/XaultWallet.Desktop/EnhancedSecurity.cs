using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop;

/// <summary>
/// Enhanced Security (Windows, off by default; Settings). While it is on:
/// <list type="bullet">
/// <item>the window is left out of screenshots, screen recordings, Recall and remote viewing
/// (SetWindowDisplayAffinity, the same call VeraCrypt's screen protection makes);</item>
/// <item>only input from a real keyboard, mouse, touchscreen or pen operates it (<see cref="InputTrust"/>):
/// what another program injects or posts, the way hidden-VNC malware drives a window, is dropped;</item>
/// <item>it answers no accessibility requests, so other programs can neither read its text nor press its
/// buttons through UI Automation;</item>
/// <item>other programs can't read or write its memory, or the wallet backend's (<see cref="ProcessHardening"/>);</item>
/// <item>and it won't start on a desktop other than the one the user sees (<see cref="OnInteractiveDesktop"/>).</item>
/// </list>
/// None of this is a promise against a program that can rewrite the app or its files: it takes away the
/// easy ways common malware watches and drives a wallet.
/// </summary>
internal static class EnhancedSecurity
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    // Kept in a static: the window holds only the delegate the hook list was given.
    private static readonly Win32Properties.CustomWndProcHookCallback Hook = OnWindowMessage;
    private static Window? _window;

    /// <summary>The window is protected right now.</summary>
    public static bool IsActive => _window is not null;

    /// <summary>Protect <paramref name="window"/> (and this process's memory, for the rest of the run).
    /// Call once the window has opened.</summary>
    public static void Activate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!IsSupported || _window is not null)
        {
            return;
        }

        ProcessHardening.Enable();
        IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || !(SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) || SetWindowDisplayAffinity(hwnd, WDA_MONITOR)))
        {
            Log.WarnOnce("enhanced-capture", $"Enhanced Security couldn't keep the window out of screen captures (error {Marshal.GetLastWin32Error()}).");
        }

        Win32Properties.AddWndProcHookCallback(window, Hook);
        _window = window;
        try
        {
            UiaDisconnectAllProviders(); // anyone already holding the window's automation tree loses it
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Stop protecting the window. The memory protection stays until the app is restarted.</summary>
    public static void Deactivate()
    {
        if (_window is not { } window)
        {
            return;
        }

        IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowDisplayAffinity(hwnd, WDA_NONE);
        }

        Win32Properties.RemoveWndProcHookCallback(window, Hook);
        _window = null;
    }

    /// <summary>
    /// Whether this process runs on the desktop the user sees and types into: WinSta0\Default. Malware
    /// that remote-controls apps without being seen (hidden VNC) starts them on a desktop of its own.
    /// <paramref name="where"/> names the desktop it is on. Anything that can't be read counts as "no".
    /// </summary>
    public static bool OnInteractiveDesktop(out string where)
    {
        where = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string station = ObjectName(GetProcessWindowStation());
        string desktop = ObjectName(GetThreadDesktop(GetCurrentThreadId()));
        where = station + "\\" + desktop;
        return station.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) && desktop.Equals("Default", StringComparison.OrdinalIgnoreCase);
    }

    private static IntPtr OnWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETOBJECT)
        {
            handled = true; // no accessibility tree: the system's frame-only default is all anyone gets
            return IntPtr.Zero;
        }

        if (!InputTrust.IsAction(msg))
        {
            return IntPtr.Zero;
        }

        bool sent = InSendMessageEx(IntPtr.Zero) != ISMEX_NOSEND;
        int? origin = GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE source) ? source.originId : null;
        if (InputTrust.IsTrusted(msg, sent, origin))
        {
            return IntPtr.Zero;
        }

        handled = true;
        Log.WarnOnce("enhanced-input", "Enhanced Security ignored input that didn't come from a keyboard, mouse, touchscreen or pen.");
        return IntPtr.Zero;
    }

    private static string ObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        byte[] buffer = new byte[512];
        return GetUserObjectInformationW(handle, UOI_NAME, buffer, buffer.Length, out int needed) && needed >= 2
            ? Encoding.Unicode.GetString(buffer, 0, Math.Min(needed, buffer.Length)).TrimEnd('\0')
            : string.Empty;
    }

    private const uint WDA_NONE = 0x00;
    private const uint WDA_MONITOR = 0x01;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint WM_GETOBJECT = 0x003D;
    private const uint ISMEX_NOSEND = 0;
    private const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_MESSAGE_SOURCE
    {
        public int deviceType;
        public int originId;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE source);

    [DllImport("user32.dll")]
    private static extern uint InSendMessageEx(IntPtr reserved);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] byte[] info, int length, out int needed);

    [DllImport("UIAutomationCore.dll")]
    private static extern int UiaDisconnectAllProviders();
}
