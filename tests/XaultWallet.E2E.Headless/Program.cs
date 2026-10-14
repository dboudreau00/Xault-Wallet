using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using XaultWallet.Desktop;
using XaultWallet.Desktop.Companion;
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
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(home, ".local", "share"));
        if (OperatingSystem.IsWindows())
        {
            // Windows takes the profile folders from the shell, not from these variables.
            AppServices.TestProfile.Root = home;
        }

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        AvaloniaSynchronizationContext.InstallIfNeeded();

        // The scenario creates a vault: refuse to run if any folder of the app is a real one.
        string[] outside = AppServices.TestProfile.PathsOutside(home);
        if (outside.Length > 0)
        {
            Console.WriteLine($"E2E REFUSED: not inside the throwaway profile {home}: {string.Join(", ", outside)}");
            return 2;
        }

        var shell = new MainWindowViewModel();
        var window = new MainWindow { DataContext = shell, Width = 1180, Height = 820 };
        window.Show();

        using var chain = new TestChain(daemon);
        var log = new Action<string>(line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}"));

        // The browser companion: the app listens as it does when started for real, and the scenario
        // talks to it through the real native host, a child process started the way Chrome starts it.
        // Not on Windows: there the child would look for the app in the real profile (the throwaway
        // one is this process's setting only), and the Windows run tests the published exe instead.
        CompanionServer? companion = null;
        Func<string, Task<string>>? ask = null;
        if (!OperatingSystem.IsWindows())
        {
            // As after "Connect" for Chrome in Settings: the channel answers only connected browsers.
            AppServices.Instance.Settings.ConnectedBrowsers.Add("chrome");
            var handler = new CompanionHandler(shell, () => { });
            companion = new CompanionServer(AppServices.Instance.CompanionEndpoint, handler.HandleAsync);
            companion.Start();
            ask = json => NativeHostClient.AskAsync(NativeHostStart(), json, TimeSpan.FromSeconds(90));
        }

        var driver = new HeadlessDriver(window, screenshots);
        var scenario = new WalletScenario(driver, chain, log, companion: ask);

        Task run = scenario.RunAsync();
        Pump(run);

        // For trying real browsers against the app (XW_E2E_HOLD_DIR): unlocked again, it keeps answering
        // the companion until the folder gets a "done" file, then says what arrived.
        if (Environment.GetEnvironmentVariable("XW_E2E_HOLD_DIR") is { Length: > 0 } holdDir && !run.IsFaulted && companion is not null)
        {
            Pump(HoldForBrowsersAsync(driver, scenario, home, holdDir, log));
        }
        if (companion is not null)
        {
            Pump(companion.DisposeAsync().AsTask());
        }
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

    private static async Task HoldForBrowsersAsync(HeadlessDriver driver, WalletScenario scenario, string home, string holdDir, Action<string> log)
    {
        await driver.TypeAsync("Unlock.Password", WalletScenario.MainPassword);
        await driver.ClickAsync("Unlock.Submit");
        DateTime synced = DateTime.UtcNow.AddMinutes(4);
        while (!(await driver.IsVisibleAsync("Wallet.SyncText") && (await driver.ReadTextAsync("Wallet.SyncText")).StartsWith("Synced", StringComparison.Ordinal)))
        {
            if (DateTime.UtcNow > synced)
            {
                throw new TimeoutException("the wallet didn't sync for the browser tests");
            }

            await Task.Delay(500);
        }
        string ready = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["home"] = home,
            ["config"] = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            ["data"] = Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            ["runtime"] = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            ["main"] = scenario.MainAddress,
            ["decoy"] = scenario.DecoyAddress,
        });
        File.WriteAllText(Path.Combine(holdDir, "ready.json"), ready);
        log("Holding for the browser tests: " + ready);

        DateTime until = DateTime.UtcNow.AddMinutes(20);
        while (!File.Exists(Path.Combine(holdDir, "done")) && DateTime.UtcNow < until)
        {
            await Task.Delay(500);
        }

        await driver.ClickAsync("Wallet.Tab.Send");
        log("Send tab after the browsers: " + await driver.ReadTextAsync("Send.Address") + " | " + await driver.ReadTextAsync("Send.Amount")
            + " | " + (await driver.IsVisibleAsync("Send.PaymentRequest") ? await driver.ReadTextAsync("Send.PaymentRequest") : "(no request note)"));
        await driver.ScreenshotAsync("hold-send");
        await driver.ClickAsync("Wallet.Tab.Receive");
        log("Receive list after the browsers: " + string.Join(" | ", await driver.ReadTextsAsync("Receive.Addresses")));
        await driver.ScreenshotAsync("hold-receive");
        await driver.ClickAsync("Wallet.Lock");
    }

    /// <summary>XaultWallet as Chrome starts its native host: the app's own assembly (built beside this
    /// test), run by the dotnet host with this test's runtime settings, the extension's origin as argument.</summary>
    private static ProcessStartInfo NativeHostStart()
    {
        string here = AppContext.BaseDirectory;
        string self = Path.GetFileNameWithoutExtension(typeof(Program).Assembly.Location);
        var start = new ProcessStartInfo("dotnet");
        foreach (string arg in new[]
                 {
                     "exec",
                     "--runtimeconfig", Path.Combine(here, self + ".runtimeconfig.json"),
                     "--depsfile", Path.Combine(here, self + ".deps.json"),
                     Path.Combine(here, "XaultWallet.dll"),
                     NativeHostClient.ChromeOrigin,
                 })
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }

    private static string Short(string address) => address.Length > 12 ? address[..6] + "…" + address[^6..] : address;
}
