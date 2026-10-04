using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace XaultWallet.E2E;

/// <summary>
/// Runs <see cref="WalletScenario"/> against the real published XaultWallet.exe, then closes it the
/// way a person does and checks what it left behind: no monero-wallet-rpc still running, no wallet
/// session folder in %TEMP%, nothing written next to the exe. Exit code 0 = all of it passed.
/// Arguments: the path to XaultWallet.exe (monero-wallet-rpc.exe next to it), the screenshot folder.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This end-to-end test drives XaultWallet.exe through Windows UI Automation: run it on Windows.");
            return 2;
        }

        string exe = Path.GetFullPath(args.Length > 0 ? args[0] : "XaultWallet.exe");
        string screenshots = Path.GetFullPath(args.Length > 1 ? args[1] : "e2e-screens");
        string daemon = Environment.GetEnvironmentVariable("XW_E2E_DAEMON") ?? "http://127.0.0.1:18081";
        double timeScale = double.Parse(Environment.GetEnvironmentVariable("XW_E2E_TIME_SCALE") ?? "2", CultureInfo.InvariantCulture);
        var log = new Action<string>(line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}"));

        // UI Automation reports physical pixels; be per-monitor DPI aware so the pointer lands where
        // the element is.
        SetProcessDpiAwarenessContext(new IntPtr(-4));

        // The scenario creates a vault: never let it near a real one.
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XaultWallet");
        if (Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any())
        {
            Console.Error.WriteLine($"Refusing to run: {profile} already exists. This test creates a wallet vault; run it on a fresh Windows account or VM (CI runners are).");
            return 2;
        }

        string appDir = Path.GetDirectoryName(exe)!;
        HashSet<string> appDirBefore = Directory.EnumerateFileSystemEntries(appDir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> sessionsBefore = SessionFolders().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        using var uia = new UIA3Automation();
        using var chain = new TestChain(daemon);
        log($"Launching {exe}");
        using Application app = Application.Launch(new ProcessStartInfo(exe) { WorkingDirectory = appDir, UseShellExecute = false });
        int pid = app.ProcessId;
        WalletScenario? scenario = null;
        try
        {
            Window window = app.GetMainWindow(uia, TimeSpan.FromSeconds(120))
                            ?? throw new InvalidOperationException("XaultWallet.exe showed no window within 120s.");
            log($"Window \"{window.Title}\" at {window.BoundingRectangle}; screen {GetSystemMetrics(0)}x{GetSystemMetrics(1)}");

            var driver = new UiaDriver(uia, window, pid, screenshots, log);
            await FitOnScreenAsync(window, driver, log);

            scenario = new WalletScenario(driver, chain, log, timeScale);
            try
            {
                await scenario.RunAsync();
            }
            catch (Exception ex)
            {
                problems.Add(ex.Message);
            }

            // Close it like a person: the window's own close button. The app then stops
            // monero-wallet-rpc and shreds the wallet's session folder.
            log("> Close the app with its close button");
            await driver.ClickAsync("Shell.Close");
        }
        catch (Exception ex)
        {
            problems.Add(ex.Message);
        }

        if (!WaitForExit(pid, TimeSpan.FromSeconds(60)))
        {
            problems.Add("XaultWallet.exe was still running 60s after Close");
            app.Kill();
        }
        else
        {
            log("  exited");
        }

        // Give a killed child a moment to be reaped before looking for orphans.
        await Task.Delay(2000);
        foreach (Process orphan in Process.GetProcessesByName("monero-wallet-rpc"))
        {
            problems.Add($"monero-wallet-rpc (pid {orphan.Id}) outlived the app");
            try { orphan.Kill(); } catch (InvalidOperationException) { /* already gone */ }
        }

        foreach (string dir in SessionFolders().Where(d => !sessionsBefore.Contains(d)))
        {
            problems.Add($"wallet session folder left in %TEMP%: {dir}");
        }

        foreach (string path in Directory.EnumerateFileSystemEntries(appDir).Where(p => !appDirBefore.Contains(p)))
        {
            problems.Add($"written next to the exe: {Path.GetFileName(path)}");
        }

        if (problems.Count > 0)
        {
            foreach (string p in problems)
            {
                Console.WriteLine("E2E FAILED: " + p);
            }

            return 1;
        }

        Console.WriteLine($"E2E OK (Windows, real XaultWallet.exe): every step passed, clean shutdown. Main {Short(scenario!.MainAddress)}, decoy {Short(scenario.DecoyAddress)}, tx {scenario.SentTxId}");
        return 0;
    }

    /// <summary>A hosted runner's screen can be smaller than the window (1024x768 by default):
    /// maximize with the app's own button rather than test half a window.</summary>
    private static async Task FitOnScreenAsync(Window window, UiaDriver driver, Action<string> log)
    {
        System.Drawing.Rectangle r = window.BoundingRectangle;
        if (r.Right <= GetSystemMetrics(0) && r.Bottom <= GetSystemMetrics(1) && r.Left >= 0 && r.Top >= 0)
        {
            return;
        }

        await driver.ClickAsync("Shell.Maximize");
        await Task.Delay(1000);
        log($"  maximized to fit the screen: {window.BoundingRectangle}");
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
    }

    /// <summary>monero-wallet-rpc session folders (MoneroProcessManager: %TEMP%\xaultwallet_*).</summary>
    private static IEnumerable<string> SessionFolders() =>
        Directory.EnumerateDirectories(Path.GetTempPath(), "xaultwallet_*");

    private static string Short(string address) => address.Length > 12 ? address[..6] + "…" + address[^6..] : address;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
