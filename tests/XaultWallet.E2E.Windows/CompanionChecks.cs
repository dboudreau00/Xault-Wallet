using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace XaultWallet.E2E;

/// <summary>
/// What only Windows and the real executable can show: connecting Chrome and Firefox (the registry and the
/// host manifest), monero: links opened by Windows itself, the tripwire noticing a connection repointed
/// behind the app's back, and Enhanced Security (memory closed to other programs, no accessibility tree,
/// left out of screen captures, injected clicks ignored, no start on a hidden desktop).
/// </summary>
internal static partial class Program
{
    private const string HostName = "dev.dboudreau.xaultwallet";
    private const string ChromeKey = @"Software\Google\Chrome\NativeMessagingHosts\" + HostName;
    private const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts\" + HostName;
    private const string LinkCommandKey = @"Software\Classes\monero\shell\open\command";

    /// <summary>Starts XaultWallet.exe as Chrome starts its native host.</summary>
    private static ProcessStartInfo HostStart(string exe)
    {
        var start = new ProcessStartInfo(exe);
        start.ArgumentList.Add(NativeHostClient.ChromeOrigin);
        start.ArgumentList.Add("--parent-window=0");
        return start;
    }

    /// <summary>With the app running and locked: Settings connects Chrome and Firefox and makes the app
    /// open monero: links; a link Windows opens arrives in the running app.</summary>
    private static async Task<List<string>> CheckConnectionsAsync(UiaDriver driver, string exe, Action<string> log)
    {
        var problems = new List<string>();
        log("> Settings: connect Chrome and Firefox, open monero: links with XaultWallet");
        await driver.ClickAsync("Shell.Settings");
        await driver.ClickAsync("Settings.ConnectChrome");
        await driver.ClickAsync("Settings.ConnectFirefox");
        await driver.ClickAsync("Settings.RegisterLinks");
        await driver.ScreenshotAsync("90-settings-companion");

        foreach ((string browser, string key, string list, string allowed) in new[]
                 {
                     ("Chrome", ChromeKey, "allowed_origins", NativeHostClient.ChromeOrigin),
                     ("Firefox", FirefoxKey, "allowed_extensions", "xaultwallet-companion@dboudreau.dev"),
                 })
        {
            string? manifest = Registry.CurrentUser.OpenSubKey(key)?.GetValue(null) as string;
            if (manifest is null || !File.Exists(manifest))
            {
                problems.Add($"{browser}: no host manifest registered under HKCU\\{key}");
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
            string? path = doc.RootElement.GetProperty("path").GetString();
            string[] ids = doc.RootElement.GetProperty(list).EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase) || ids.Length != 1 || ids[0] != allowed)
            {
                problems.Add($"{browser}: the manifest names {path} for [{string.Join(", ", ids)}]");
            }
        }

        // The host the Chrome manifest names answers for the locked app.
        string reply = await NativeHostClient.AskAsync(HostStart(exe), """{"v":1,"id":"win1","type":"status"}""", TimeSpan.FromSeconds(60));
        if (!reply.Contains("\"state\":\"locked\"", StringComparison.Ordinal))
        {
            problems.Add("the native host's status for the locked app: " + reply);
        }

        string? command = Registry.CurrentUser.OpenSubKey(LinkCommandKey)?.GetValue(null) as string;
        if (command != $"\"{exe}\" \"%1\"")
        {
            problems.Add("monero: links open with: " + command);
        }

        await driver.ClickAsync("Settings.Close");

        // Windows itself opens a link: a second XaultWallet starts and hands it to this one.
        log("> Windows opens a monero: link: the running app gets the payment request");
        Process.Start(new ProcessStartInfo($"monero:{KnownAddress}?tx_amount=0.1&tx_description=From%20a%20link") { UseShellExecute = true })?.Dispose();
        try
        {
            await driver.WaitForAsync("Unlock.PendingRequest", TimeSpan.FromSeconds(60), enabled: false);
            string note = await driver.ReadTextAsync("Unlock.PendingRequest");
            if (!note.Contains("payment request is waiting", StringComparison.Ordinal))
            {
                problems.Add("the unlock screen says: " + note);
            }

            await driver.ScreenshotAsync("91-link-waiting");
            await driver.ClickAsync("Unlock.DismissRequest");
        }
        catch (TimeoutException ex)
        {
            problems.Add("a monero: link opened by Windows didn't reach the app: " + ex.Message);
        }

        return problems;
    }

    /// <summary>A mainnet-format address for the link test (regtest uses the same format).</summary>
    private const string KnownAddress = "44AFFq5kSiGBoZ4NMDwYtN18obc8AemS33DBLWs3H7otXft3XjrpDtQGv7SqSsaBYBb98uNbr2VBBEt7f2wfn3RVGQBEP3A";

    /// <summary>The Chrome manifest repointed while the app was closed: the next start says so.</summary>
    private static async Task<List<string>> CheckTripwireAsync(UIA3Automation uia, string exe, string screenshots, Action<string> log)
    {
        var problems = new List<string>();
        log("> The tripwire: a browser connection repointed behind the app's back is reported at start");
        string manifest = (string)Registry.CurrentUser.OpenSubKey(ChromeKey)!.GetValue(null)!;
        string original = File.ReadAllText(manifest);
        File.WriteAllText(manifest, original.Replace(JsonEncodedText.Encode(exe).ToString(), JsonEncodedText.Encode(@"C:\Users\Public\impostor.exe").ToString(), StringComparison.Ordinal));
        try
        {
            using Application app = Application.Launch(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
            Window window = app.GetMainWindow(uia, TimeSpan.FromSeconds(120)) ?? throw new InvalidOperationException("no window");
            var driver = new UiaDriver(uia, window, app.ProcessId, screenshots, log);
            await driver.WaitForAsync("Shell.Notice", TimeSpan.FromSeconds(30), enabled: false);
            string notice = await driver.ReadTextAsync("Shell.NoticeText");
            if (!notice.Contains("Chrome connection", StringComparison.Ordinal))
            {
                problems.Add("the start-up notice says: " + notice);
            }

            await driver.ScreenshotAsync("92-tripwire");
            await driver.ClickAsync("Shell.Close");
            if (!WaitForExit(app.ProcessId, TimeSpan.FromSeconds(60)))
            {
                problems.Add("the app didn't close after the tripwire check");
                app.Kill();
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            problems.Add("the tripwire wasn't reported: " + ex.Message);
        }
        finally
        {
            File.WriteAllText(manifest, original);
        }

        return problems;
    }

    /// <summary>Enhanced Security on (as saved in settings.json), checked from outside the app.</summary>
    private static async Task<List<string>> CheckEnhancedSecurityAsync(UIA3Automation uia, string exe, string profile, int orangeBefore, Action<string> log)
    {
        var problems = new List<string>();
        log("> Enhanced Security: memory, accessibility, screen capture, injected input, hidden desktop");
        string settingsPath = Path.Combine(profile, "settings.json");
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(settingsPath))!;
        settings["EnhancedSecurity"] = JsonSerializer.SerializeToElement(true);
        settings["EnhancedSecurityConfirmed"] = JsonSerializer.SerializeToElement(true); // as after a click on Keep it on
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));
        string logFile = Path.Combine(profile, "logs", "xaultwallet.log");

        using Application app = Application.Launch(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
        try
        {
            Window window = app.GetMainWindow(uia, TimeSpan.FromSeconds(120)) ?? throw new InvalidOperationException("no window");
            await Task.Delay(6000); // the startup screen gives way to the unlock screen
            window.SetForeground();
            await Task.Delay(1500);

            // 1. Memory: closed to this test (another program of the same user).
            IntPtr read = OpenProcess(0x0010 /* PROCESS_VM_READ */, false, app.ProcessId);
            int error = Marshal.GetLastWin32Error();
            if (read != IntPtr.Zero)
            {
                CloseHandle(read);
                if (!DebugPrivilegeEnabled())
                {
                    problems.Add("another program could still open XaultWallet's memory for reading");
                }
                else
                {
                    log("  this test holds the debug privilege: the memory check can't be made here");
                }
            }
            else if (error != 5)
            {
                problems.Add($"opening XaultWallet's memory failed with {error}, not access denied");
            }

            // 2. Accessibility: the window is there, its contents aren't.
            if (window.FindFirstDescendant(cf => cf.ByAutomationId("Unlock.Password")) is not null)
            {
                problems.Add("UI Automation still sees the unlock screen's password box");
            }

            // 3. Screen capture: the unlock button's orange was on screen before, isn't in a capture now.
            int orangeNow = OrangePixels(window.BoundingRectangle);
            log($"  orange pixels in a capture of the window: {orangeBefore} before, {orangeNow} with Enhanced Security");
            if (orangeBefore < 200 || orangeNow > orangeBefore / 20)
            {
                problems.Add($"screen capture: {orangeBefore} orange pixels before, {orangeNow} with Enhanced Security");
            }

            // 4. Injected input: a SendInput click on the window is dropped (the app logs it once).
            Rectangle r = window.BoundingRectangle;
            Mouse.Click(new Point(r.Left + (r.Width / 2), r.Top + (r.Height / 2)));
            await Task.Delay(1500);
            if (!File.ReadAllText(logFile).Contains("Enhanced Security ignored input", StringComparison.Ordinal))
            {
                problems.Add("an injected click wasn't refused (nothing in the log)");
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            problems.Add("Enhanced Security: " + ex.Message);
        }
        finally
        {
            // Closing is a window message, not input: it still works.
            using (Process p = Process.GetProcessById(app.ProcessId))
            {
                p.CloseMainWindow();
            }

            if (!WaitForExit(app.ProcessId, TimeSpan.FromSeconds(60)))
            {
                problems.Add("the app with Enhanced Security didn't close on WM_CLOSE");
                app.Kill();
            }
        }

        // 5. A hidden desktop (how hidden-VNC malware runs apps): XaultWallet refuses to start there.
        int code = RunOnHiddenDesktop(exe);
        if (code != 4)
        {
            problems.Add($"started on a hidden desktop, XaultWallet exited with {code}, not 4");
        }
        else if (!File.ReadAllText(logFile).Contains("Enhanced Security: refused to start", StringComparison.Ordinal))
        {
            problems.Add("the refusal on a hidden desktop isn't in the log");
        }
        else
        {
            log("  refused to start on a hidden desktop (exit 4)");
        }

        // 6. The way out when a computer's devices aren't seen as devices: "--no-enhanced-security"
        // turns it off, and the app starts without it.
        problems.AddRange(await StartAndCloseAsync(uia, exe, ["--no-enhanced-security"], "--no-enhanced-security", log));
        if (ReadSetting(settingsPath, "EnhancedSecurity"))
        {
            problems.Add("--no-enhanced-security left Enhanced Security on in settings.json");
        }

        // 7. Turned on but never confirmed (the app closed during the countdown): the next start leaves it off and says so.
        settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(settingsPath))!;
        settings["EnhancedSecurity"] = JsonSerializer.SerializeToElement(true);
        settings["EnhancedSecurityConfirmed"] = JsonSerializer.SerializeToElement(false);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));
        problems.AddRange(await StartAndCloseAsync(uia, exe, [], "Enhanced Security is off", log));
        if (ReadSetting(settingsPath, "EnhancedSecurity"))
        {
            problems.Add("an unconfirmed Enhanced Security was still on after a start");
        }

        return problems;
    }

    private static bool ReadSetting(string settingsPath, string name) =>
        JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Starts the app (with <paramref name="args"/>), waits for its window, checks a notice
    /// containing <paramref name="expect"/> when one is given (not for a command-line flag), and closes it.</summary>
    private static async Task<List<string>> StartAndCloseAsync(UIA3Automation uia, string exe, string[] args, string expect, Action<string> log)
    {
        var problems = new List<string>();
        var start = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Application app = Application.Launch(start);
        try
        {
            Window window = app.GetMainWindow(uia, TimeSpan.FromSeconds(120)) ?? throw new InvalidOperationException("no window");
            if (args.Length == 0)
            {
                var driver = new UiaDriver(uia, window, app.ProcessId, Path.GetTempPath(), log);
                await driver.WaitForAsync("Shell.Notice", TimeSpan.FromSeconds(30), enabled: false);
                string notice = await driver.ReadTextAsync("Shell.NoticeText");
                if (!notice.Contains(expect, StringComparison.Ordinal))
                {
                    problems.Add("the start-up notice says: " + notice);
                }
            }

            log($"  started with {(args.Length == 0 ? "no arguments" : string.Join(" ", args))}: {expect}");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            problems.Add($"{expect}: {ex.Message}");
        }
        finally
        {
            using (Process p = Process.GetProcessById(app.ProcessId))
            {
                p.CloseMainWindow();
            }

            if (!WaitForExit(app.ProcessId, TimeSpan.FromSeconds(60)))
            {
                problems.Add($"{expect}: the app didn't close");
                app.Kill();
            }
        }

        return problems;
    }

    /// <summary>What the uninstallers run: every registration that leads to this program goes, and the
    /// runner's registry is left as it was found.</summary>
    private static List<string> CheckUninstallCleanup(string exe, Action<string> log)
    {
        var problems = new List<string>();
        log("> The uninstallers' cleanup: --remove-registrations");
        string? chromeManifest = Registry.CurrentUser.OpenSubKey(ChromeKey)?.GetValue(null) as string;
        using (Process cleanup = Process.Start(new ProcessStartInfo(exe, "--remove-registrations") { UseShellExecute = false })!)
        {
            if (!cleanup.WaitForExit(60_000))
            {
                cleanup.Kill();
                problems.Add("--remove-registrations didn't finish within a minute");
            }
            else if (cleanup.ExitCode != 0)
            {
                problems.Add($"--remove-registrations exited with {cleanup.ExitCode}");
            }
        }

        foreach (string key in new[] { ChromeKey, FirefoxKey, LinkCommandKey })
        {
            if (Registry.CurrentUser.OpenSubKey(key) is { } left)
            {
                left.Dispose();
                problems.Add($"HKCU\\{key} is still there after --remove-registrations");
            }
        }

        if (chromeManifest is not null && File.Exists(chromeManifest))
        {
            problems.Add("the Chrome host manifest is still there after --remove-registrations");
        }

        return problems;
    }

    /// <summary>Pixels of the app's orange in a capture of <paramref name="area"/> (sampled every 2 px).</summary>
    internal static int OrangePixels(Rectangle area)
    {
        using CaptureImage image = Capture.Rectangle(area);
        Bitmap bitmap = image.Bitmap;
        int count = 0;
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                Color c = bitmap.GetPixel(x, y);
                if (c.R > 200 && c.G is > 80 and < 140 && c.B < 80)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int RunOnHiddenDesktop(string exe)
    {
        const uint GENERIC_ALL = 0x10000000;
        IntPtr desktop = CreateDesktopW("xw-e2e-hidden", IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
        {
            return -Marshal.GetLastWin32Error();
        }

        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"WinSta0\xw-e2e-hidden" };
            if (!CreateProcessW(null, "\"" + exe + "\"", IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out PROCESS_INFORMATION pi))
            {
                return -Marshal.GetLastWin32Error();
            }

            try
            {
                if (WaitForSingleObject(pi.hProcess, 60_000) != 0)
                {
                    TerminateProcess(pi.hProcess, 1);
                    return -258; // still running after a minute
                }

                GetExitCodeProcess(pi.hProcess, out uint exitCode);
                return (int)exitCode;
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>Whether this process holds the debug privilege, enabled. PrivilegeCheck wants an
    /// impersonation token, so the process token is duplicated as one first.</summary>
    private static bool DebugPrivilegeEnabled()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008 | 0x0002 /* TOKEN_QUERY | TOKEN_DUPLICATE */, out IntPtr token))
        {
            return false;
        }

        IntPtr impersonation = IntPtr.Zero;
        try
        {
            if (!DuplicateToken(token, 2 /* SecurityImpersonation */, out impersonation) || !LookupPrivilegeValueW(null, "SeDebugPrivilege", out long luid))
            {
                return false;
            }

            var set = new PRIVILEGE_SET { PrivilegeCount = 1, Control = 1, Luid = luid };
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PRIVILEGE_SET
    {
        public uint PrivilegeCount;
        public uint Control;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr attributes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, string commandLine, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir,
        ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool PrivilegeCheck(IntPtr token, ref PRIVILEGE_SET privileges, out bool result);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateToken(IntPtr token, int impersonationLevel, out IntPtr duplicate);
}
