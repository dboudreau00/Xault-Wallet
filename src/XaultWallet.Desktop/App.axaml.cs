using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Desktop.ViewModels;
using XaultWallet.Desktop.Views;

namespace XaultWallet.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
        }

        base.OnFrameworkInitializationCompleted();
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
