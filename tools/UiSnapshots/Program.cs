using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using XaultWallet.Desktop;
using XaultWallet.Desktop.ViewModels;
using XaultWallet.Desktop.Views;

namespace XaultWallet.UiSnapshots;

/// <summary>
/// Renders every screen of the app headlessly (real Skia output, no display) with deterministic
/// demo data and writes one PNG per screen. Any binding error makes the run fail, so this doubles
/// as a smoke test that every view loads against its view-model.
/// </summary>
internal static class Program
{
    private static readonly List<string> BindingErrors = new();

    private static int Main(string[] args)
    {
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "ui-snapshots");
        Directory.CreateDirectory(outDir);

        // Never touch the real user profile: settings/vault paths resolve under a throwaway HOME.
        string home = Directory.CreateTempSubdirectory("xw-ui-").FullName;
        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("APPDATA", home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        Logger.Sink = new BindingErrorSink();

        var shots = new (string name, Func<ViewModelBase> screen, Action<Window>? arrange)[]
        {
            ("startup", Startup, null),
            ("create-vault", CreateVault, null),
            ("create-vault-seed", CreateVaultWithSeed, w => ScrollTo(w, 330)),
            ("create-vault-duress", CreateVaultWithSeed, w => ScrollToEnd(w)),
            ("unlock", () => new UnlockViewModel(), null),
            ("wallet-receive", Wallet, w => SelectTab(w, 0)),
            ("wallet-send", WalletSend, w => SelectTab(w, 1)),
            ("wallet-send-confirm", WalletSendConfirm, w => SelectTab(w, 1)),
            ("wallet-history", Wallet, w => SelectTab(w, 2)),
            ("wallet-upgraded", WalletUpgraded, w => SelectTab(w, 0)),
            ("settings", () => new SettingsViewModel(walletOpen: false), null),
        };

        foreach ((string name, Func<ViewModelBase> screen, Action<Window>? arrange) in shots)
        {
            var window = new MainWindow { DataContext = new MainWindowViewModel(screen()), Width = 1080, Height = 760 };
            window.Show();
            Settle();
            if (arrange is not null)
            {
                arrange(window);
                Settle();
            }

            string path = Path.Combine(outDir, name + ".png");
            using (var frame = window.CaptureRenderedFrame())
            {
                frame!.Save(path);
            }

            Console.WriteLine("wrote " + path);
            if (name == "wallet-receive" && window.DataContext is MainWindowViewModel { Current: WalletViewModel wallet })
            {
                VerifyQr(path, wallet.ReceiveUri);
            }

            window.Close();
        }

        CheckHistoryRowSettles();

        try { Directory.Delete(home, recursive: true); } catch { /* temp only */ }

        foreach (string f in Failures)
        {
            Console.WriteLine("FAIL: " + f);
        }

        if (Failures.Count > 0)
        {
            return 1;
        }

        if (BindingErrors.Count > 0)
        {
            Console.WriteLine($"FAIL: {BindingErrors.Count} binding error(s):");
            foreach (string e in BindingErrors.Distinct())
            {
                Console.WriteLine("  " + e);
            }

            return 1;
        }

        Console.WriteLine("OK: all screens rendered with zero binding errors.");
        return 0;
    }

    private static readonly List<string> Failures = new();

    /// <summary>
    /// A row that reaches its 10th confirmation must stop saying "9/10". The redraw is skipped for
    /// settled rows (to keep the scroll position), so the boundary itself is what has to be checked.
    /// </summary>
    private static void CheckHistoryRowSettles()
    {
        WalletViewModel vm = WalletViewModel.ForPreview(new WalletSecrets { Network = MoneroNetwork.Stagenet });
        vm.SetHistory([new TransferEntry { TxId = FakeTxId(9), Type = "in", Amount = 1, Height = 1_000, Timestamp = 1 }]);
        vm.Height = 1_009;
        string before = vm.History[0].Subtitle;
        vm.Height = 1_010;
        string after = vm.History[0].Subtitle;
        if (before.Contains("9/10", StringComparison.Ordinal) && after.Contains("block 1,000", StringComparison.Ordinal))
        {
            Console.WriteLine("History ok: a row redraws as settled at its 10th confirmation.");
        }
        else
        {
            Failures.Add($"History row did not settle: at 9 conf '{before}', at 10 conf '{after}'");
        }
    }

    /// <summary>
    /// Decode the rendered Receive screen with zbar and require EXACTLY the URI for the address on
    /// screen. A QR that disagrees with the visible address would send funds somewhere else.
    /// </summary>
    private static void VerifyQr(string png, string expected)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("zbarimg", ["--raw", "-q", png])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string decoded = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (decoded == expected)
            {
                Console.WriteLine("QR ok: decodes to exactly the displayed address URI.");
            }
            else
            {
                Failures.Add($"QR decoded to '{decoded}', expected '{expected}'");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("WARN: zbarimg not installed; QR round-trip not verified.");
        }
    }

    /// <summary>Let layout, bindings and entrance animations finish before capturing.</summary>
    private static void Settle()
    {
        for (int i = 0; i < 12; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(60);
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static void SelectTab(Window w, int index)
    {
        foreach (TabControl tabs in w.GetVisualDescendants().OfType<TabControl>())
        {
            tabs.SelectedIndex = index;
        }
    }

    private static void ScrollTo(Window w, double y)
    {
        foreach (ScrollViewer sv in w.GetVisualDescendants().OfType<ScrollViewer>())
        {
            sv.Offset = new Vector(0, y);
        }
    }

    private static void ScrollToEnd(Window w)
    {
        foreach (ScrollViewer sv in w.GetVisualDescendants().OfType<ScrollViewer>())
        {
            sv.Offset = new Vector(0, sv.Extent.Height);
        }
    }

    // ---------------- demo data (deterministic; not real wallets) ----------------

    private static string FakeAddress(char prefix, int seed)
    {
        const string b58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var rng = new Random(seed);
        return prefix + new string(Enumerable.Range(0, 94).Select(_ => b58[rng.Next(b58.Length)]).ToArray());
    }

    private static string FakeTxId(int seed)
    {
        var rng = new Random(seed);
        return string.Concat(Enumerable.Range(0, 32).Select(_ => rng.Next(256).ToString("x2")));
    }

    private static ViewModelBase Startup() => new StartupViewModel(preview: true)
    {
        Status = "Contacting your node…",
        Detail = "Monero 'Fluorine Fermi' (v0.18.3.1)",
        Checking = true,
    };

    private static ViewModelBase CreateVault() => new CreateWalletViewModel
    {
        MainPassword = "granite-otter-lantern-41",
        MainPasswordConfirm = "granite-otter-lantern-41",
    };

    private static ViewModelBase CreateVaultWithSeed()
    {
        var vm = new CreateWalletViewModel();
        string[] words = ("sober tawny pebbles lunar ought cavernous vixen rally fuming eclipse oars " +
                          "hydrogen vowels nabbing oyster pyramid duke vulture lukewarm tunnel " +
                          "efficient luggage gearbox glass tunnel").Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            vm.RealSeedWords.Add(new SeedWord(i + 1, words[i]));
        }

        vm.RealSeedGenerated = true;
        vm.RealVerified = true;
        vm.MainPassword = vm.MainPasswordConfirm = "granite-otter-lantern-41";
        vm.EnableDuress = true;
        return vm;
    }

    private static WalletViewModel Wallet()
    {
        var vm = WalletViewModel.ForPreview(new WalletSecrets
        {
            Network = MoneroNetwork.Stagenet,
            RestoreHeight = 1_580_000,
            DaemonAddress = "http://127.0.0.1:38081",
        });

        vm.IsReady = true;
        vm.Balance = 12.483017420331m;
        vm.UnlockedBalance = 11.983017420331m;
        vm.PrimaryAddress = FakeAddress('5', 7);
        vm.Height = 1_712_404;
        vm.DaemonHeight = 1_712_404;
        vm.IsSynced = true;
        vm.SyncProgress = 100;
        vm.SyncText = "Synced · block 1,712,404";

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var history = new[]
        {
            new TransferEntry { TxId = FakeTxId(1), Type = "pool", Amount = 500_000_000_000, Fee = 0, Height = 0, Timestamp = (ulong)(now - 300) },
            new TransferEntry { TxId = FakeTxId(2), Type = "out", Amount = 1_250_000_000_000, Fee = 30_660_000, Height = 1_712_380, Timestamp = (ulong)(now - 4_000) },
            new TransferEntry { TxId = FakeTxId(3), Type = "in", Amount = 8_000_000_000_000, Fee = 0, Height = 1_711_902, Timestamp = (ulong)(now - 90_000) },
            new TransferEntry { TxId = FakeTxId(4), Type = "in", Amount = 5_233_017_420_331, Fee = 0, Height = 1_709_115, Timestamp = (ulong)(now - 400_000) },
        };
        vm.SetHistory(history);
        return vm;
    }

    private static ViewModelBase WalletUpgraded()
    {
        WalletViewModel vm = Wallet();
        vm.UpgradeNotice = WalletViewModel.LegacyUpgradeNotice;
        return vm;
    }

    private static ViewModelBase WalletSend()
    {
        WalletViewModel vm = Wallet();
        vm.SendAddress = FakeAddress('5', 11);
        vm.SendAmountText = "0,25";
        return vm;
    }

    private static ViewModelBase WalletSendConfirm()
    {
        WalletViewModel vm = Wallet();
        vm.SendAddress = FakeAddress('5', 11);
        vm.SendAmountText = "0.25";
        vm.ConfirmSendAddress = vm.SendAddress;
        vm.SendSummary = "Low priority · built and signed, not yet broadcast";
        vm.ConfirmAmountText = "0.25 XMR";
        vm.SendFeeText = "0.00003066 XMR";
        vm.SendTotalText = "0.25003066 XMR";
        vm.ShowSendConfirm = true;
        return vm;
    }

    private sealed class BindingErrorSink : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            BindingErrors.Add($"[{area}] {messageTemplate} ({source?.GetType().Name})");

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            BindingErrors.Add($"[{area}] {Format(messageTemplate, propertyValues)} ({source?.GetType().Name})");

        private static string Format(string template, object?[] values)
        {
            int i = 0;
            return System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]+\}", _ => i < values.Length ? values[i++]?.ToString() ?? "null" : "?");
        }
    }
}
