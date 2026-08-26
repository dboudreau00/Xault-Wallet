using Avalonia;
using XaultWallet.Core.Diagnostics;

namespace XaultWallet.Desktop;

internal static class Program
{
    // Held for the process lifetime; released by the OS on exit.
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        // Two instances would race on vault.xv and settings.json, spawn two wallet-rpc
        // children, and defeat the orphaned-temp-dir sweep. First one in wins.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\XaultWallet.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Log.Initialize(AppServices.Instance.LogsDirectory);
            Log.Warn("Another XaultWallet instance is already running; exiting.");
            return 2;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
