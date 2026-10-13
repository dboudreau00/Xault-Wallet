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

            // Graceful shutdown: cancel the first close, run async cleanup (kill the
            // monero-wallet-rpc child and shred temp files), then actually close. Without
            // this the child could be orphaned and secrets left in temp.
            window.Closing += async (_, e) =>
            {
                if (cleaned)
                {
                    return;
                }

                e.Cancel = true;
                try
                {
                    window.RememberPlacement(AppServices.Instance.Settings);
                    if (_companion is not null)
                    {
                        await _companion.DisposeAsync(); // no more browser requests or links
                    }

                    await SensitiveClipboard.ClearIfStillOursAsync();
                    await mainVm.ShutdownAsync();
                }
                catch (Exception ex)
                {
                    Log.Error("Error during shutdown cleanup", ex);
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
            if (AppServices.Instance.Settings.EnhancedSecurity && EnhancedSecurity.IsSupported)
            {
                window.Opened += (_, _) => EnhancedSecurity.Activate(window);
            }

            // The browser companion and monero: links (a second launch hands its link over here).
            var handler = new CompanionHandler(mainVm, () => BringToFront(window));
            _companion = new CompanionServer(AppServices.Instance.CompanionEndpoint, handler.HandleAsync);
            _companion.NameTaken += () => Dispatcher.UIThread.Post(mainVm.CompanionNameTaken);
            _companion.Start();
            if (Program.LaunchLink is { } link)
            {
                mainVm.OfferLaunchLink(link);
            }

            // The tripwire: the connections this app set up must still lead to it.
            Dispatcher.UIThread.Post(mainVm.CheckRegistrations, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
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
