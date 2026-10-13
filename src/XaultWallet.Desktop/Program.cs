using System.Security.Cryptography;
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

    /// <summary>Signalled by a second launch so the running instance brings its window forward
    /// (Windows only: named events are not supported by .NET on other platforms).</summary>
    internal const string ActivateEventName = @"Local\XaultWallet.Activate";

    /// <summary>The monero: link this launch was started with (the system opening a link in
    /// XaultWallet), for the app to put in the Send tab once a wallet is open.</summary>
    internal static string? LaunchLink { get; private set; }

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

        string? launchLink = args.Length == 1 && MoneroUri.IsUri(args[0]) ? args[0].Trim() : null;
        bool enhanced = AppServices.Instance.Settings.EnhancedSecurity && EnhancedSecurity.IsSupported;
        if (enhanced)
        {
            // Hidden-VNC malware starts apps on a desktop of its own, out of the user's sight.
            if (!EnhancedSecurity.OnInteractiveDesktop(out string where))
            {
                Log.Initialize(AppServices.Instance.LogsDirectory);
                Log.Warn($"Enhanced Security: refused to start on the desktop \"{where}\", which isn't the one on screen.");
                return 4;
            }

            ProcessHardening.Enable(); // before anything secret is in memory
        }

        // Two instances would race on vault.xv and settings.json, spawn two wallet-rpc
        // children, and defeat the orphaned-temp-dir sweep. First one in wins.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\XaultWallet.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Log.Initialize(AppServices.Instance.LogsDirectory);
            Log.Warn("Another XaultWallet instance is already running; asking it to come forward.");
            DetachedLauncher.AllowAnyForeground(); // started by the shell or the browser: let it take the foreground
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

        LaunchLink = launchLink;
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
    private static AppBuilder BuildAvaloniaApp(bool overlayPopups) => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions { OverlayPopups = overlayPopups })
        .WithInterFont()
        .LogToTrace();

    /// <summary>The native messaging host: messages from the browser on standard input, replies on
    /// standard output, each passed through the running app's companion endpoint.</summary>
    private static int RunNativeHost(CompanionCaller caller)
    {
        var endpoint = CompanionEndpoint.ForDataDirectory(AppServices.DefaultDataDirectory());
        DetachedLauncher.AllowAnyForeground(); // started by the browser the user is clicking in
        var host = new NativeHost(caller,
            (line, ct) => CompanionClient.SendAsync(endpoint, line, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), ct),
            () => AppServices.ExecutablePath is { } exe && DetachedLauncher.Launch(exe));
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        return host.RunAsync(input, output).GetAwaiter().GetResult();
    }

    private static int RemoveRegistrations()
    {
        try
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
                if (BrowserRegistration.Status(browser, exe, paths, registry) == RegistrationState.Registered)
                {
                    BrowserRegistration.Unregister(browser, paths, registry);
                }
            }

            MoneroLinkRegistration.Unregister(exe, paths, registry); // only when it is this program's
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            // An uninstall must never fail on this.
        }

        return 0;
    }

    /// <summary>A second launch: the link it was started with (or just "come forward") goes to the copy
    /// already running. False when that copy didn't answer.</summary>
    private static bool HandToRunningInstance(string? link)
    {
        try
        {
            var request = new CompanionRequest(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                link is null ? CompanionRequestType.Show : CompanionRequestType.Pay, CompanionCaller.Link, link ?? string.Empty);
            return CompanionClient.SendAsync(AppServices.Instance.CompanionEndpoint, CompanionProtocol.Serialize(request),
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() is not null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidDataException)
        {
            return false;
        }
    }
}
