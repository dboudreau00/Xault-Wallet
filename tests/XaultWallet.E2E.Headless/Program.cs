using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using XaultWallet.Desktop;
using XaultWallet.Desktop.ViewModels;
using XaultWallet.Desktop.Views;

namespace XaultWallet.E2E;

/// <summary>
/// Runs <see cref="WalletScenario"/> against the real app in-process (headless, real rendering).
/// Exit code 0 = every step passed. Screenshots land in the directory given as the first argument.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string screenshots = Path.GetFullPath(args.Length > 0 ? args[0] : "e2e-screens");
        string daemon = Environment.GetEnvironmentVariable("XW_E2E_DAEMON") ?? "http://127.0.0.1:18081";

        // A brand-new profile every run, never the developer's real vault. The config root does not
        // exist yet, exactly like a fresh account.
        string home = Directory.CreateTempSubdirectory("xw-e2e-").FullName;
        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(home, ".config"));

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        AvaloniaSynchronizationContext.InstallIfNeeded();

        var shell = new MainWindowViewModel();
        var window = new MainWindow { DataContext = shell, Width = 1180, Height = 820 };
        window.Show();

        using var chain = new TestChain(daemon);
        var log = new Action<string>(line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}"));
        var scenario = new WalletScenario(new HeadlessDriver(window, screenshots), chain, log);

        Task run = scenario.RunAsync();
        Pump(run);
        Pump(shell.ShutdownAsync()); // stops monero-wallet-rpc and shreds its session folder
        window.Close();

        try { Directory.Delete(home, recursive: true); } catch { /* temp only */ }

        if (run.IsFaulted)
        {
            Console.WriteLine("E2E FAILED: " + run.Exception!.GetBaseException().Message);
            return 1;
        }

        Console.WriteLine($"E2E OK: every step passed. Main {Short(scenario.MainAddress)}, decoy {Short(scenario.DecoyAddress)}, tx {scenario.SentTxId}");
        return 0;
    }

    /// <summary>The scenario awaits on the UI thread: keep the dispatcher and render loop turning.</summary>
    private static void Pump(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static string Short(string address) => address.Length > 12 ? address[..6] + "…" + address[^6..] : address;
}
