using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop;

/// <summary>
/// Enhanced Security (Windows, off by default; Settings). While it is on:
/// <list type="bullet">
/// <item>the window is left out of screenshots, screen recordings, Recall and screen sharing
/// (SetWindowDisplayAffinity, the same call VeraCrypt's screen protection makes);</item>
/// <item>only key presses and clicks the system reports as coming from a device (or from the system, or
/// from a UIAccess program) act on it (<see cref="InputTrust"/>): what another program injects or posts, the
/// way hidden-VNC malware drives a window, is dropped; typed characters and mouse moves are not checked
/// (moves count toward staying unlocked only when they come from a device);</item>
/// <item>it answers no accessibility requests, so other programs can neither read its text nor press its
/// buttons through UI Automation;</item>
/// <item>other programs can't open its memory, or the wallet backend's (<see cref="ProcessHardening"/> says
/// what that leaves open);</item>
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

    // What the last Activate achieved: Windows can refuse each part, and the user is told which.
    private static bool _captureExcluded;
    private static bool _inputFiltered;
    private static bool _hookSeen;

    /// <summary>The message being handled came from a device (kept up to date by the window's filter).</summary>
    private static bool _lastInputFromDevice = true;

    /// <summary>While it is on, input that didn't come from a device doesn't count as the user being
    /// there: a program posting mouse moves can't keep the wallet from locking itself.</summary>
    public static bool IgnoresActivity => IsActive && !_lastInputFromDevice;

    // Since it was turned on: has a key press or click from a device arrived yet, and how many were dropped before it.
    private static bool _deviceSeen;
    private static int _droppedBeforeDevice;

    /// <summary>Three key presses or clicks were dropped and none has come from a device since it was turned
    /// on: perhaps this computer's keyboard or mouse isn't seen as one (raised once, on the UI thread).</summary>
    public static event Action? InputIgnoredRepeatedly;

    /// <summary>After that, the first key press or click from a device arrived: all is well.</summary>
    public static event Action? InputFromDevice;

    /// <summary>The process-wide part, at start before anything secret is in memory (or when turned on).</summary>
    public static void HardenProcess() => ProcessHardening.Enable();

    /// <summary>Protect <paramref name="window"/> (and this process's memory, for the rest of the run).
    /// Call once the window has opened.</summary>
    public static void Activate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!IsSupported || _window is not null)
        {
            return;
        }

        HardenProcess();

        // Menus, lists and tooltips opened from now on are drawn inside the window, under its protection.
        Program.PlatformOptions.OverlayPopups = true;

        IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && !SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
        {
            SetWindowDisplayAffinity(hwnd, WDA_MONITOR); // older Windows: shown black in captures
        }

        _captureExcluded = hwnd != IntPtr.Zero && GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity != WDA_NONE;
        if (!_captureExcluded)
        {
            Log.WarnOnce("enhanced-capture", $"Enhanced Security couldn't keep the window out of screen captures (error {Marshal.GetLastWin32Error()}).");
        }

        Win32Properties.AddWndProcHookCallback(window, Hook);
        _window = window;

        // The filter proves it sits in the window's path: a message sent to the window must pass through it.
        _hookSeen = false;
        _deviceSeen = false;
        _droppedBeforeDevice = 0;
        if (hwnd != IntPtr.Zero)
        {
            SendMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }

        _inputFiltered = _hookSeen;
        if (!_inputFiltered)
        {
            Log.WarnOnce("enhanced-hook", "Enhanced Security couldn't place its input filter on the window.");
        }

        try
        {
            UiaDisconnectAllProviders(); // anyone already holding the window's automation tree loses it
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>What didn't work when it was turned on, in a sentence for the user; null when all of it did.</summary>
    public static string? Shortfall()
    {
        if (!IsSupported || _window is null)
        {
            return null;
        }

        var missing = new List<string>();
        if (!_captureExcluded)
        {
            missing.Add("screenshots and screen sharing can still show the window");
        }

        if (!_inputFiltered)
        {
            missing.Add("input other programs fake isn't filtered, and accessibility tools can still read the window");
        }

        if (!ProcessHardening.SelfProtected)
        {
            missing.Add("other programs can still read XaultWallet's memory");
        }

        return missing.Count == 0 ? null : string.Join("; ", missing) + ". The log has the details.";
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
        _lastInputFromDevice = true;
        Program.PlatformOptions.OverlayPopups = false;
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

        if (msg == WM_NULL)
        {
            _hookSeen = true;
            return IntPtr.Zero;
        }

        bool action = InputTrust.IsAction(msg);
        if (!action && msg is not (WM_MOUSEMOVE or WM_NCMOUSEMOVE))
        {
            return IntPtr.Zero;
        }

        bool sent = InSendMessageEx(IntPtr.Zero) != ISMEX_NOSEND;
        int? origin = GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE source) ? source.originId : null;
        _lastInputFromDevice = InputTrust.IsFromDevice(sent, origin);
        if (!action)
        {
            return IntPtr.Zero;
        }

        if (InputTrust.IsTrusted(msg, sent, origin))
        {
            if (!_deviceSeen)
            {
                _deviceSeen = true;
                if (_droppedBeforeDevice >= 3)
                {
                    InputFromDevice?.Invoke();
                }
            }

            return IntPtr.Zero;
        }

        handled = true;
        if (!_deviceSeen && ++_droppedBeforeDevice == 3)
        {
            InputIgnoredRepeatedly?.Invoke(); // the user may be shut out: say how to get back in
        }

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
    private const uint WM_NULL = 0x0000;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_NCMOUSEMOVE = 0x00A0;
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    // Not a system-wide known DLL: loaded from System32 only, never from the app's own folder.
    [DllImport("UIAutomationCore.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int UiaDisconnectAllProviders();
}
