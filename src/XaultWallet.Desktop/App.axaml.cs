using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Desktop.Companion;
using XaultWallet.Desktop.ViewModels;
using XaultWallet.Desktop.Views;

namespace XaultWallet.Desktop;

public partial class App : Application
{
    private CompanionServer? _companion;

    public override void Initialize()
    {
        Motion.RegisterAnimators(); // before any style animation can start
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Initialize(AppServices.Instance.LogsDirectory);
        Log.Info("XaultWallet starting.");
        InstallGlobalExceptionHandlers();

        // A previous session that crashed or was killed may have left a restored wallet in
        // temp. Safe to sweep here: the single-instance guard in Program.Main guarantees no
        // other live session owns one of these directories. Off the UI thread — shredding a
        // large orphaned wallet cache (random-overwrite + per-file fsync) must not stall the
        // first paint. A dir the sweep is mid-shredding can't collide with a NEW wallet dir:
        // fresh dirs fail the sweep's 15-minute quiet-period check.
        _ = Task.Run(XaultWallet.Core.Monero.MoneroProcessManager.ShredOrphanedTempDirs);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = mainVm };
            window.RestorePlacement(AppServices.Instance.Settings);

            // Built-in Tor starts with the app, Wasabi-style, so it is usually connected by the
            // time a password has been typed. Its status shows in Settings; wallets wait for it.
            if (AppServices.Instance.Settings.UseBuiltInTor)
            {
                Dispatcher.UIThread.Post(() => _ = StartTorAsync(), DispatcherPriority.Background);
            }

            bool cleaned = false;
            bool cleaning = false;

            // Graceful shutdown: cancel the first close, run async cleanup (kill the
            // monero-wallet-rpc child and shred temp files), then actually close. Without
            // this the child could be orphaned and secrets left in temp. A second click on
            // close while that runs waits for it; nothing before the shutdown can skip it.
            window.Closing += async (_, e) =>
            {
                if (cleaned)
                {
                    return;
                }

                e.Cancel = true;
                if (cleaning)
                {
                    return;
                }

                cleaning = true;
                try
                {
                    // Each step on its own: one that fails doesn't skip the ones after it (the last one
                    // stops the wallet backends and shreds their temporary files).
                    await ShutdownStepAsync("remembering the window", () =>
                    {
                        window.RememberPlacement(AppServices.Instance.Settings);
                        return Task.CompletedTask;
                    });
                    if (_companion is not null)
                    {
                        await ShutdownStepAsync("closing the browser channel", () => _companion.DisposeAsync().AsTask());
                    }

                    await ShutdownStepAsync("clearing the clipboard", () => SensitiveClipboard.ClearIfStillOursAsync());
                    await ShutdownStepAsync("stopping the wallets", () => mainVm.ShutdownAsync());
                }
                finally
                {
                    cleaned = true;
                    Log.Info("XaultWallet shut down.");
                    await Dispatcher.UIThread.InvokeAsync(window.Close);
                }
            };

            desktop.MainWindow = window;
            ListenForSecondLaunch(window);

            // Enhanced Security (Settings): the window is protected from the moment it exists.
            mainVm.ApplyEnhancedSecurity = on =>
            {
                if (on)
                {
                    EnhancedSecurity.Activate(window);
                }
                else
                {
                    EnhancedSecurity.Deactivate();
                }
            };
            mainVm.EnhancedSecurityShortfall = EnhancedSecurity.Shortfall;
            if (Program.EnhancedAtStart)
            {
                window.Opened += (_, _) => EnhancedSecurity.Activate(window);
            }
            else if (Program.EnhancedNotConfirmed)
            {
                mainVm.EnhancedSecurityNotConfirmed();
            }

            EnhancedSecurity.InputIgnoredRepeatedly += () => Dispatcher.UIThread.Post(mainVm.EnhancedInputIgnored);
            EnhancedSecurity.InputFromDevice += () => Dispatcher.UIThread.Post(mainVm.EnhancedInputConfirmed);
            if (Program.SettingsWereDamaged)
            {
                mainVm.SettingsWereDamaged();
            }

            // The browser companion and monero: links (a second launch hands its link over here).
            var handler = new CompanionHandler(mainVm, () => BringToFront(window));
            _companion = new CompanionServer(AppServices.Instance.CompanionEndpoint, handler.HandleAsync);
            _companion.NameTaken += () => Dispatcher.UIThread.Post(mainVm.CompanionNameTaken);
            if (AppServices.Instance.CompanionEndpoint.IsUsable)
            {
                _companion.Start();
            }
            else
            {
                Log.Warn("The browser companion's channel can't be made here: the path to the data folder is too long for a socket.");
                mainVm.CompanionUnavailable();
            }
            if (Program.LaunchLink is { } link)
            {
                mainVm.OfferLaunchLink(link);
            }

            // The tripwire: the connections this app set up must still lead to it. Checked at start and
            // again (at most once a minute) when the window comes back to the front.
            Dispatcher.UIThread.Post(() => _ = CheckRegistrationsAsync(mainVm), DispatcherPriority.Background);
            long lastCheck = Environment.TickCount64;
            window.Activated += (_, _) =>
            {
                if (Environment.TickCount64 - lastCheck >= 60_000)
                {
                    lastCheck = Environment.TickCount64;
                    Dispatcher.UIThread.Post(() => _ = CheckRegistrationsAsync(mainVm), DispatcherPriority.Background);
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShutdownStepAsync(string what, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            Log.Error("Error during shutdown cleanup (" + what + ")", ex);
        }
    }

    private static async Task CheckRegistrationsAsync(MainWindowViewModel shell)
    {
        try
        {
            await shell.CheckRegistrationsAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Checking the browser connections failed", ex);
        }
    }

    private static async Task StartTorAsync()
    {
        try
        {
            await AppServices.Instance.Tor.EnsureStartedAsync();
        }
        catch (TorNotReadyException)
        {
            // Shown in Settings and on any wallet that waits for it; logged by the controller.
        }
    }

    /// <summary>Un-minimise and focus the window: a payment request from the browser or a link is
    /// waiting in it.</summary>
    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>A second launch signals this instance (see Program.Main): un-minimise and focus the
    /// existing window instead of leaving the user wondering why nothing happened.</summary>
    private static void ListenForSecondLaunch(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
            var thread = new Thread(() =>
            {
                while (signal.WaitOne())
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (window.WindowState == WindowState.Minimized)
                        {
                            window.WindowState = WindowState.Normal;
                        }

                        window.Activate();
                    });
                }
            })
            {
                IsBackground = true,
                Name = "XaultWallet.ActivateListener",
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("Second-launch listener unavailable: " + ex.GetType().Name);
        }
    }

    private static void InstallGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved(); // don't let a background task crash the process
        };
    }
}
