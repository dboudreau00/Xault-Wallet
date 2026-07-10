using Avalonia;
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
        }

        base.OnFrameworkInitializationCompleted();
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
