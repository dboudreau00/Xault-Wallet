using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using XaultWallet.Core.Companion;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop;

internal static class Program
{
    // Held for the process lifetime; released by the OS on exit.
    private static Mutex? _singleInstanceMutex;

    private const string SingleInstanceName = @"Local\XaultWallet.SingleInstance";

    /// <summary>Signalled by a second launch so the running instance brings its window forward
    /// (Windows only: named events are not supported by .NET on other platforms).</summary>
    internal const string ActivateEventName = @"Local\XaultWallet.Activate";

    /// <summary>The monero: link this launch was started with (the system opening a link in
    /// XaultWallet), for the app to put in the Send tab once a wallet is open.</summary>
    internal static string? LaunchLink { get; private set; }

    /// <summary>Enhanced Security protects this run (it was on, and confirmed, at start).</summary>
    internal static bool EnhancedAtStart { get; private set; }

    /// <summary>Enhanced Security was on but never confirmed, so this run started without it.</summary>
    internal static bool EnhancedNotConfirmed { get; private set; }

    /// <summary>The settings file couldn't be read, so this run started with the defaults.</summary>
    internal static bool SettingsWereDamaged { get; private set; }

    /// <summary>The options Avalonia's Windows backend was started with: Enhanced Security turned on
    /// later sets <see cref="Win32PlatformOptions.OverlayPopups"/> on this same instance.</summary>
    internal static Win32PlatformOptions PlatformOptions { get; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        // A browser starting XaultWallet as the companion extension's native messaging host: no window,
        // no vault, only messages passed to the running app (see NativeHost). Started this way by anything
        // but the companion extension, it stops here.
        CompanionCaller? caller = NativeHost.TryIdentifyCaller(args, out bool hostLaunch);
        if (hostLaunch)
        {
            return caller is { } browser ? RunNativeHost(browser) : 3;
        }

        // The uninstallers: remove the browser connections and the monero: link handler that point at
        // this program (and only those), so nothing is left pointing at a deleted file.
        if (args is ["--remove-registrations"])
        {
            return RemoveRegistrations();
        }

        // Loading the settings says whether the file was damaged (the next save forgets it).
        AppSettings settings = AppServices.Instance.Settings;
        SettingsWereDamaged = AppSettings.RecoveredFromCorruptFile;

        // The way back when Enhanced Security doesn't take this computer's keyboard or mouse for devices:
        // "XaultWallet.exe --no-enhanced-security" turns it off, then starts as usual. Not while a copy is
        // running: that copy would write its own setting back when it closes.
        if (args is ["--no-enhanced-security"])
        {
            if (IsAppRunning())
            {
                Tell("XaultWallet is still running. Close it first (from the taskbar, or in Task Manager if it doesn't respond), then start it again with --no-enhanced-security.");
                return 2;
            }

            DisableEnhancedSecurity();
            args = [];
        }

        string? launchLink = args.Length == 1 && MoneroUri.IsUri(args[0]) ? CompanionProtocol.CleanPaymentUri(args[0]) : null;
        bool unconfirmed = settings.EnhancedSecurity && !settings.EnhancedSecurityConfirmed;
        bool enhanced = settings.EnhancedSecurity && settings.EnhancedSecurityConfirmed && EnhancedSecurity.IsSupported;
        if (enhanced)
        {
            // Hidden-VNC malware starts apps on a desktop of its own, out of the user's sight.
            if (!EnhancedSecurity.OnInteractiveDesktop(out string where))
            {
                Log.Initialize(AppServices.Instance.LogsDirectory);
                Log.Warn($"Enhanced Security: refused to start on the desktop \"{where}\", which isn't the one on screen.");
                return 4;
            }

            Log.Initialize(AppServices.Instance.LogsDirectory); // so a protection that fails is on record
            EnhancedSecurity.HardenProcess(); // before anything secret is in memory
        }

        // Two instances would race on vault.xv and settings.json, spawn two wallet-rpc
        // children, and defeat the orphaned-temp-dir sweep. First one in wins.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceName, out bool createdNew);
        if (!createdNew)
        {
            Log.Initialize(AppServices.Instance.LogsDirectory);
            Log.Warn("Another XaultWallet instance is already running; asking it to come forward.");
            if (!HandToRunningInstance(launchLink)
                && OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(ActivateEventName, out EventWaitHandle? activate))
            {
                using (activate)
                {
                    activate.Set();
                }
            }

            return 2;
        }

        if (unconfirmed)
        {
            // Turned on last time but never confirmed: the app was closed during the countdown, maybe
            // because nothing could be clicked. This run starts without it, and says so.
            settings.EnhancedSecurity = false;
            TrySaveSettings();
            EnhancedNotConfirmed = true;
        }

        LaunchLink = launchLink;
        EnhancedAtStart = enhanced;
        try
        {
            BuildAvaloniaApp(overlayPopups: enhanced).StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            // Last-resort net: without this, a UI-thread exception kills the process with
            // nothing in the log. Never rethrows secrets — Log writes type + message only.
            Log.Initialize(AppServices.Instance.LogsDirectory);
            Log.Error("Fatal: the application crashed", ex);
            Console.Error.WriteLine($"XaultWallet crashed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("Details were written to the log folder: " + AppServices.Instance.LogsDirectory);
            return 1;
        }
        finally
        {
            GC.KeepAlive(_singleInstanceMutex);
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(overlayPopups: false);

    /// <param name="overlayPopups">Draw menus, lists and tooltips inside the main window instead of in
    /// windows of their own (Enhanced Security: then they share its protection from screen capture and
    /// from input that didn't come from a device).</param>
    private static AppBuilder BuildAvaloniaApp(bool overlayPopups)
    {
        PlatformOptions.OverlayPopups = overlayPopups;
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(PlatformOptions)
            .WithInterFont()
            .LogToTrace();
    }

    /// <summary>The native messaging host: messages from the browser on standard input, replies on
    /// standard output, each passed through the running app's companion endpoint. Never a crash dialog in
    /// front of the browser: whatever goes wrong ends the host, and the extension says it couldn't connect.</summary>
    private static int RunNativeHost(CompanionCaller caller)
    {
        try
        {
            var endpoint = CompanionEndpoint.ForDataDirectory(AppServices.DefaultDataDirectory());
            var host = new NativeHost(caller,
                (line, ct) => CompanionClient.SendAsync(endpoint, line, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), ct),
                () => AppServices.ExecutablePath is { } exe && DetachedLauncher.Launch(exe),
                appRunning: IsAppRunning);
            using Stream input = Console.OpenStandardInput();
            using Stream output = Console.OpenStandardOutput();
            return host.RunAsync(input, output).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("XaultWallet's browser host stopped: " + ex.GetType().Name);
            return 1;
        }
    }

    /// <summary>A copy of the app holds the single-instance lock (it may still be starting, or be busy).</summary>
    private static bool IsAppRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(SingleInstanceName, out Mutex? running))
            {
                running.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // it exists; this process just may not open it
        }
        catch (Exception ex) when (ex is IOException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Each registration that points at this program is removed on its own: one that can't be
    /// read or removed doesn't stop the others, and an uninstall never fails on this.</summary>
    private static int RemoveRegistrations()
    {
        if (AppServices.ExecutablePath is not { } exe)
        {
            return 0;
        }

        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "XaultWallet");
        BrowserPaths paths = BrowserPaths.ForCurrentUser(local);
        IUserRegistry? registry = OperatingSystem.IsWindows() ? new WindowsUserRegistry() : null;
        foreach (CompanionBrowser browser in Enum.GetValues<CompanionBrowser>())
        {
            Step("the " + browser + " connection", () =>
            {
                if (BrowserRegistration.Status(browser, exe, paths, registry) == RegistrationState.Registered)
                {
                    BrowserRegistration.Unregister(browser, paths, registry);
                }
            });
        }

        Step("the monero: link handler", () => MoneroLinkRegistration.Unregister(exe, paths, registry)); // only when it is this program's
        return 0;

        static void Step(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                Log.Initialize(Path.Combine(AppServices.DefaultDataDirectory(), "logs")); // only when there is something to say
                Log.Warn($"Uninstall: couldn't remove {what}: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>Turns Enhanced Security off in the settings, for a start that couldn't otherwise be operated
    /// (only when no copy is running).</summary>
    private static void DisableEnhancedSecurity()
    {
        AppSettings settings = AppServices.Instance.Settings;
        settings.EnhancedSecurity = false;
        settings.EnhancedSecurityConfirmed = false;
        TrySaveSettings();
        Log.Initialize(AppServices.Instance.LogsDirectory);
        Log.Info("Enhanced Security turned off from the command line.");
    }

    private static void TrySaveSettings()
    {
        try
        {
            AppServices.Instance.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Initialize(AppServices.Instance.LogsDirectory);
            Log.Error("Saving settings failed", ex);
        }
    }

    /// <summary>A second launch: the link it was started with (or just "come forward") goes to the copy
    /// already running, which may still be starting (it is given a few seconds to listen). False when that
    /// copy didn't take it: then its window is brought forward instead, where it says why.</summary>
    private static bool HandToRunningInstance(string? link)
    {
        try
        {
            var request = new CompanionRequest(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                link is null ? CompanionRequestType.Show : CompanionRequestType.Pay, CompanionCaller.Link, link ?? string.Empty);
            byte[]? reply = CompanionClient.SendAsync(AppServices.Instance.CompanionEndpoint, CompanionProtocol.Serialize(request),
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            return reply is not null && Took(reply);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The running copy answered ok (a refusal is "not taken").</summary>
    private static bool Took(byte[] reply)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(reply);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A message for someone who started XaultWallet from a shortcut or a command line: a dialog on
    /// Windows (an app there has no console), standard error elsewhere.</summary>
    private static void Tell(string message)
    {
        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(IntPtr.Zero, message, "XaultWallet", MB_ICONWARNING);
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }

    private const uint MB_ICONWARNING = 0x30;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
