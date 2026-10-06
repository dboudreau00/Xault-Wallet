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
internal static partial class Program
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
        // Deliberately a config folder that does NOT exist yet (a fresh Linux account, a minimal
        // install, a container): the data folder must still land inside it, never in the CWD.
        string configRoot = Path.Combine(home, "config-not-created-yet");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configRoot);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        Logger.Sink = new BindingErrorSink();

        var shots = new (string name, Func<ViewModelBase> screen, Action<Window>? arrange)[]
        {
            ("startup", Startup, null),
            ("startup-setup", StartupNeedsBackend, null),
            ("startup-installing", StartupInstalling, null),
            ("create-vault", CreateVault, null),
            ("create-vault-seed", CreateVaultWithSeed, w => ScrollTo(w, 330)),
            ("create-vault-duress", CreateVaultWithSeed, w => ScrollToEnd(w)),
            ("unlock", () => new UnlockViewModel(), null),
            ("wallet-receive", Wallet, w => SelectTab(w, 0)),
            ("wallet-receive-request", WalletRequest, w => SelectTab(w, 0)),
            ("wallet-receive-addresses", WalletRequest, w => { SelectTab(w, 0); ScrollToEnd(w); }),
            ("wallet-send", WalletSend, w => SelectTab(w, 1)),
            ("wallet-send-multi", WalletSendMulti, w => SelectTab(w, 1)),
            ("wallet-send-confirm", WalletSendConfirm, w => SelectTab(w, 1)),
            ("wallet-send-confirm-multi", WalletSendConfirmMulti, w => SelectTab(w, 1)),
            ("wallet-sent", WalletSent, w => SelectTab(w, 1)),
            ("wallet-send-error", WalletSendError, w => SelectTab(w, 1)),
            ("wallet-history", Wallet, w => SelectTab(w, 2)),
            ("wallet-history-details", WalletHistoryDetails, w => SelectTab(w, 2)),
            ("wallet-contacts", Wallet, w => SelectTab(w, 3)),
            ("wallet-contacts-edit", WalletContactEditor, w => SelectTab(w, 3)),
            ("wallet-tools", WalletTools, w => SelectTab(w, 4)),
            ("wallet-tools-proofs", WalletTools, w => { SelectTab(w, 4); Settle(); ScrollToEnd(w); }),
            ("wallet-manage", WalletManage, w => SelectTab(w, 0)),
            ("wallet-manage-backup", WalletManageSecrets, w => { SelectTab(w, 0); Settle(); ScrollToEnd(w); }),
            ("wallet-accounts", WalletAccounts, w => SelectTab(w, 0)),
            ("wallet-watch-only", WalletWatchOnly, w => SelectTab(w, 1)),
            ("wallet-upgraded", WalletUpgraded, w => SelectTab(w, 0)),
            ("add-wallet", AddWalletNew, null),
            ("add-wallet-seed", AddWalletNewWithSeed, w => ScrollTo(w, 240)),
            ("add-wallet-keys", AddWalletKeys, null),
            ("add-wallet-confirm", AddWalletConfirm, null),
            ("settings", () => new SettingsViewModel(), null),
            ("settings-vault-format", SettingsVaultFormat, w => ScrollTo(w, 520)),
            ("settings-rpc-installed", () => SettingsAfterInstall(ok: true), null),
            ("settings-rpc-install-failed", () => SettingsAfterInstall(ok: false), null),
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
            CheckAccessibleNames(name, window);
            CheckPasswordsStayPrivate(name, window);
            CheckEntrancesSettled(name, window);
            if (name is "wallet-receive" or "wallet-receive-request" && window.DataContext is MainWindowViewModel { Current: WalletViewModel wallet })
            {
                VerifyQr(path, wallet.ReceiveUri);
                CheckBalanceIsAnnounced(window, wallet.BalanceDisplay);
            }

            window.Close();
        }

        CheckHistoryRowSettles();
        CheckMiningRewardRow();
        CheckSwitcherItemsAreNamed();
        CheckSwitcherKeysOpenTheList();
        CheckContactEditsReachSendForms();
        CheckHideAmountsEverywhere();
        CheckNoteDraftSurvivesRebuild();
        CheckManageOpensWithSecretsHidden();
        CheckAccountAddressTitles();
        CheckSameWalletIsRecognised();
        CheckAddWalletWhileTheNodeIsSlow();
        CheckSameSeedOnAnotherNetwork();
        CheckOverspendKeepsTheBalanceHidden();
        CheckTemporaryBackendsEnd();
        CheckMotion();
        if (!OperatingSystem.IsWindows())
        {
            CheckDataFolderIsUnder(configRoot);
        }

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

        if (CheckedSecrets == 0)
        {
            Console.WriteLine("FAIL: no filled password box was rendered, so password privacy went unchecked.");
            return 1;
        }

        Console.WriteLine($"Privacy ok: {CheckedSecrets} filled password boxes, none readable through UI Automation.");
        Console.WriteLine("OK: all screens rendered with zero binding errors.");
        return 0;
    }

    private static readonly List<string> Failures = new();

    /// <summary>The vault's folder must be absolute and under the user's config root, even when that
    /// root didn't exist at startup (it used to resolve to "XaultWallet" in the current directory).</summary>
    private static void CheckDataFolderIsUnder(string configRoot)
    {
        string data = AppServices.Instance.DataDirectory;
        if (Path.IsPathFullyQualified(data) && data.StartsWith(configRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Directory.Exists(data))
        {
            Console.WriteLine("Data folder ok: created under a config root that didn't exist yet.");
        }
        else
        {
            Failures.Add($"Data folder is '{data}', expected a folder under '{configRoot}'");
        }
    }

    /// <summary>
    /// The wallet switcher's items must be announced by the wallet's name: that is what a screen
    /// reader says, and what the Windows end-to-end test selects by (an item's content is a data
    /// object, so without a name it is announced as nothing).
    /// </summary>
    private static void CheckSwitcherItemsAreNamed()
    {
        WalletViewModel vm = Wallet();
        var window = new MainWindow { DataContext = new MainWindowViewModel(vm), Width = 1080, Height = 760 };
        window.Show();
        Settle();
        ComboBox switcher = window.GetVisualDescendants().OfType<ComboBox>()
            .First(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "Wallet.Switcher");
        switcher.IsDropDownOpen = true;
        Settle();
        string[] expected = vm.Profile.Wallets.Select(w => w.Name).ToArray();
        string[] announced = switcher.GetRealizedContainers()
            .Select(c => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName())
            .ToArray();
        switcher.IsDropDownOpen = false;
        window.Close();
        if (announced.SequenceEqual(expected))
        {
            Console.WriteLine($"Accessibility ok: the wallet switcher announces {string.Join(", ", announced)}.");
        }
        else
        {
            Failures.Add($"Wallet switcher items are announced as [{string.Join(", ", announced)}], expected [{string.Join(", ", expected)}]");
        }
    }

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
    /// Motion must never leave the UI transparent or displaced. Entrances start hidden (no flash of
    /// the final frame), actually move, and end exactly on resting values; a screen change ends with
    /// the new screen fully opaque and in place and the old one hidden.
    /// </summary>
    private static void CheckMotion()
    {
        var shell = new MainWindowViewModel(new UnlockViewModel());
        var window = new MainWindow { DataContext = shell, Width = 1080, Height = 760 };
        window.Show();
        Pump(0);
        Control card = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "LoginCard");
        double first = card.Opacity;
        Pump(320);
        double middle = card.Opacity;
        bool moving = card.RenderTransform is Avalonia.Media.Transformation.TransformOperations { IsIdentity: false };
        Pump(900);
        bool rested = card.Opacity == 1 && IsResting(card);
        if (first < 0.05 && middle is > 0.05 and < 0.99 && moving && rested)
        {
            Console.WriteLine($"Motion ok: entrance went {first:0.00} -> {middle:0.00} (moving) -> 1 at rest.");
        }
        else
        {
            Failures.Add($"Entrance: opacity {first:0.00} -> {middle:0.00} (moving={moving}) -> {card.Opacity:0.00}, at rest={rested}");
        }

        // Screen change: the new screen must not flash in at full opacity, and must end at rest.
        // Measured before the first frame is rendered: the layout pass that swaps the screens has
        // run, the render tick has not.
        var settings = new SettingsViewModel();
        shell.Current = settings;
        window.UpdateLayout();
        var presenters = window.GetVisualDescendants().OfType<TransitioningContentControl>().First()
            .GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .Where(p => p.Name is "PART_ContentPresenter" or "PART_ContentPresenter2").ToList();
        var to = presenters.First(p => ReferenceEquals(p.Content, settings));
        var from = presenters.First(p => !ReferenceEquals(p, to));
        double toFirst = to.Opacity;
        Pump(1000);
        if (toFirst < 0.05 && to.IsVisible && to.Opacity == 1 && IsResting(to) && !from.IsVisible)
        {
            Console.WriteLine($"Motion ok: screen change starts at {toFirst:0.00}, ends opaque and in place; old screen hidden.");
        }
        else
        {
            Failures.Add($"Screen change: new screen {toFirst:0.00} -> {to.Opacity:0.00} (at rest={IsResting(to)}, visible={to.IsVisible}), old visible={from.IsVisible}");
        }

        window.Close();
    }

    /// <summary>A masked text box must not hand its text to other programs through UI Automation
    /// (Avalonia's TextBox does, password or not: any process in the session could read it).</summary>
    private static void CheckPasswordsStayPrivate(string screen, Window window)
    {
        foreach (TextBox box in window.GetVisualDescendants().OfType<TextBox>().Where(b => b.PasswordChar != default && !string.IsNullOrEmpty(b.Text)))
        {
            var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(box);
            string? exposed = peer.GetProvider<Avalonia.Automation.Provider.IValueProvider>()?.Value;
            if (!string.IsNullOrEmpty(exposed))
            {
                Failures.Add($"[{screen}] {Avalonia.Automation.AutomationProperties.GetAutomationId(box)} exposes its password through UI Automation");
            }
            else
            {
                CheckedSecrets++;
            }
        }
    }

    private static int CheckedSecrets;

    /// <summary>Every element with an entrance must come to rest (fully opaque, no transform): a
    /// stuck entrance is a blank screen. Entrances still playing get up to 3 s (the longest, on the
    /// startup screen, takes 1 s); one that is stuck never gets there.</summary>
    private static void CheckEntrancesSettled(string screen, Window window)
    {
        List<Control> Unsettled() => window.GetVisualDescendants().OfType<Control>()
            .Where(c => Entrance.HasEntrance(c) && c.IsEffectivelyVisible && (c.Opacity < 1 || !IsResting(c)))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<Control> unsettled = Unsettled();
        while (unsettled.Count > 0 && sw.ElapsedMilliseconds < 3000)
        {
            Pump(50);
            unsettled = Unsettled();
        }

        foreach (Control c in unsettled)
        {
            Failures.Add($"[{screen}] {c.GetType().Name}.{string.Join('.', c.Classes)} stuck at opacity {c.Opacity:0.000}, transform {c.RenderTransform}");
        }
    }

    private static bool IsResting(Visual v) =>
        v.RenderTransform is null or Avalonia.Media.Transformation.TransformOperations { IsIdentity: true };

    /// <summary>Run the UI for <paramref name="milliseconds"/> of real time (animations use the clock).</summary>
    private static void Pump(int milliseconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        while (sw.ElapsedMilliseconds < milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Every control a person can operate must have a real accessible name: what a screen reader
    /// says, and what UI Automation tests find. A button whose content is an icon plus text and no
    /// AutomationProperties.Name is announced as its content's type ("Avalonia.Controls.StackPanel").
    /// </summary>
    private static void CheckAccessibleNames(string screen, Window window)
    {
        var interactive = new HashSet<Avalonia.Automation.Peers.AutomationControlType>
        {
            Avalonia.Automation.Peers.AutomationControlType.Button,
            Avalonia.Automation.Peers.AutomationControlType.CheckBox,
            Avalonia.Automation.Peers.AutomationControlType.RadioButton,
            Avalonia.Automation.Peers.AutomationControlType.ComboBox,
            Avalonia.Automation.Peers.AutomationControlType.Edit,
            Avalonia.Automation.Peers.AutomationControlType.TabItem,
            Avalonia.Automation.Peers.AutomationControlType.Slider,
            Avalonia.Automation.Peers.AutomationControlType.Spinner,
        };
        var bad = new List<string>();

        void Walk(Avalonia.Automation.Peers.AutomationPeer peer)
        {
            // A scroll bar's arrow buttons are framework parts nobody tabs to.
            if (peer is Avalonia.Automation.Peers.ControlAutomationPeer { Owner: Avalonia.Controls.Primitives.ScrollBar })
            {
                return;
            }

            if (peer.IsControlElement() && interactive.Contains(peer.GetAutomationControlType()))
            {
                string name = peer.GetName().Trim();
                if (name.Length == 0 || name.StartsWith("Avalonia.", StringComparison.Ordinal) || name.StartsWith("XaultWallet.", StringComparison.Ordinal))
                {
                    string id = peer.GetAutomationId() ?? "(no id)";
                    bad.Add($"{peer.GetAutomationControlType()} {id} is announced as \"{name}\"");
                }
            }

            foreach (Avalonia.Automation.Peers.AutomationPeer child in peer.GetChildren())
            {
                Walk(child);
            }
        }

        Walk(Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(window));
        foreach (string b in bad.Distinct())
        {
            Failures.Add($"[{screen}] {b}");
        }
    }

    /// <summary>
    /// What a screen reader (and UI Automation) gets for the balance must be the balance. It is drawn
    /// as two Runs, and a plain TextBlock made of Runs reports an empty accessible name in Avalonia 11.1.
    /// </summary>
    private static void CheckBalanceIsAnnounced(Window window, string expected)
    {
        Control? balance = window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "Wallet.Balance");
        string announced = balance is null
            ? "(not found)"
            : Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(balance).GetName();
        if (announced == expected && expected.Length > 0)
        {
            Console.WriteLine($"Accessibility ok: the balance is announced as \"{announced}\".");
        }
        else
        {
            Failures.Add($"The balance is announced as \"{announced}\", expected \"{expected}\"");
        }
    }

    /// <summary>
    /// A coinbase output (solo / P2Pool payout; wallet-rpc type "block") is money IN, labelled as a
    /// mining reward, and stays "confirming" until its 60th block, not the 10th. It used to render as
    /// an outgoing "−0.6 XMR" titled "block".
    /// </summary>
    private static void CheckMiningRewardRow()
    {
        WalletViewModel vm = WalletViewModel.ForPreview(new WalletSecrets { Network = MoneroNetwork.Stagenet });
        vm.SetHistory([new TransferEntry { TxId = FakeTxId(10), Type = "block", Amount = 600_000_000_000, Height = 2_000, Timestamp = 1 }]);
        vm.Height = 2_059;
        HistoryRow confirming = vm.History[0];
        vm.Height = 2_060;
        HistoryRow settled = vm.History[0];
        bool ok = confirming.IsIncoming && !confirming.IsOutgoing
                  && confirming.Title == "Mining reward"
                  && confirming.AmountText == "+0.6 XMR"
                  && confirming.Subtitle.Contains("59/60", StringComparison.Ordinal)
                  && settled.Subtitle.Contains("block 2,000", StringComparison.Ordinal);
        if (ok)
        {
            Console.WriteLine("History ok: a mining reward is incoming and confirms over 60 blocks.");
        }
        else
        {
            Failures.Add($"Mining reward row wrong: '{confirming.Title}' '{confirming.AmountText}' incoming={confirming.IsIncoming} " +
                         $"at 59 conf '{confirming.Subtitle}', at 60 conf '{settled.Subtitle}'");
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

    /// <summary>Let layout, bindings and entrance animations finish before capturing (the longest
    /// entrance, the startup screen's last fade, takes 1 s).</summary>
    private static void Settle()
    {
        for (int i = 0; i < 20; i++)
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

    private static StartupViewModel StartupNeedsBackend() => new(preview: true)
    {
        Status = "XaultWallet needs monero-wallet-rpc",
        Checking = false,
        CanContinue = true,
        NeedsBackend = true,
    };

    private static ViewModelBase StartupInstalling()
    {
        StartupViewModel vm = StartupNeedsBackend();
        vm.Setup.Installing = true;
        vm.Setup.ProgressKnown = true;
        vm.Setup.Progress = 45;
        vm.Setup.StageText = "Downloading the official Monero CLI… 41.2 of 91.6 MB";
        return vm;
    }

    private static ViewModelBase SettingsAfterInstall(bool ok)
    {
        var vm = new SettingsViewModel();
        vm.Setup.Succeeded = ok;
        vm.Setup.ResultText = ok
            ? "Installed monero-wallet-rpc 0.18.5.1. binaryFate's signature and the download's checksum were verified, and it ran on this system."
            : "The download doesn't match the checksum Monero signed, so it was thrown away and nothing was installed. Try again later, or download and verify monero-wallet-rpc yourself.";
        return vm;
    }

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

    /// <summary>A vault profile with three wallets (the first on screen) and two contacts.</summary>
    private static WalletProfile DemoProfile()
    {
        var everyday = new WalletSecrets
        {
            Name = "Everyday",
            Network = MoneroNetwork.Stagenet,
            RestoreHeight = 1_580_000,
            DaemonAddress = "http://127.0.0.1:38081",
            Labels = { ["0/1"] = "Alice (freelance)", ["0/2"] = "Shop sales" },
            AccountLabels = { [1] = "Business" },
            TxNotes = { [FakeTxId(2)] = "Rent, July", [FakeTxId(3)] = "Invoice 2024-117" },
        };
        var savings = new WalletSecrets { Name = "Savings", Network = MoneroNetwork.Stagenet, Kind = WalletKind.Keys, Address = FakeAddress('5', 21) };
        var donations = new WalletSecrets { Name = "Donations (watch)", Network = MoneroNetwork.Stagenet, Kind = WalletKind.ViewOnly, Address = FakeAddress('5', 22) };
        return new WalletProfile
        {
            Wallets = { everyday, savings, donations },
            Contacts =
            {
                new Contact { Name = "Bob's Books", Address = FakeAddress('5', 31), Note = "Order payments" },
                new Contact { Name = "Carol", Address = FakeAddress('7', 32) },
            },
            ActiveWalletId = everyday.Id,
        };
    }

    private static WalletViewModel Wallet() => Fill(ProfileViewModel.ForPreview(DemoProfile()).Start());

    /// <summary>Demo state for a wallet screen (no backend behind it).</summary>
    private static WalletViewModel Fill(WalletViewModel vm)
    {
        vm.IsReady = true;
        vm.Balance = 12.483017420331m;
        vm.UnlockedBalance = 11.983017420331m;
        vm.PrimaryAddress = FakeAddress('5', 7);
        vm.ReceiveAddress = vm.PrimaryAddress;
        vm.Addresses.Add(new AddressRow(0, 0, vm.PrimaryAddress, "", used: true) { IsShown = true });
        vm.Addresses.Add(new AddressRow(0, 1, FakeAddress('7', 8), "Alice (freelance)", used: true));
        vm.Addresses.Add(new AddressRow(0, 2, FakeAddress('7', 9), "Shop sales", used: false));
        vm.Addresses.Add(new AddressRow(0, 3, FakeAddress('7', 10), "", used: false));
        vm.Height = 1_712_404;
        vm.DaemonHeight = 1_712_404;
        vm.IsSynced = true;
        vm.SyncProgress = 100;
        vm.SyncText = "Synced · block 1,712,404";

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var history = new[]
        {
            new TransferEntry { TxId = FakeTxId(1), Type = "pool", Amount = 500_000_000_000, Fee = 0, Height = 0, Timestamp = (ulong)(now - 300) },
            new TransferEntry
            {
                TxId = FakeTxId(2), Type = "out", Amount = 1_250_000_000_000, Fee = 30_660_000, Height = 1_712_380, Timestamp = (ulong)(now - 4_000),
                Destinations = [new TransferDestination { Address = FakeAddress('5', 31), Amount = 1_250_000_000_000 }],
            },
            new TransferEntry { TxId = FakeTxId(5), Type = "block", Amount = 612_000_000_000, Fee = 0, Height = 1_712_371, Timestamp = (ulong)(now - 4_100) },
            new TransferEntry
            {
                TxId = FakeTxId(3), Type = "in", Amount = 8_000_000_000_000, Fee = 0, Height = 1_711_902, Timestamp = (ulong)(now - 90_000),
                SubaddrIndex = new SubaddressIndex { Major = 0, Minor = 1 },
            },
            new TransferEntry { TxId = FakeTxId(4), Type = "in", Amount = 5_233_017_420_331, Fee = 0, Height = 1_709_115, Timestamp = (ulong)(now - 400_000) },
        };
        vm.SetHistory(history);
        return vm;
    }

    /// <summary>Settings from a wallet of a vault made by 0.3: the format card, upgrade asked for.</summary>
    private static ViewModelBase SettingsVaultFormat()
    {
        var vm = new SettingsViewModel(ProfileViewModel.ForPreview(DemoProfile(), oldFormat: true));
        vm.AskFormatUpgradeCommand.Execute(null);
        return vm;
    }

    private static ViewModelBase WalletUpgraded()
    {
        WalletViewModel vm = Wallet();
        vm.Profile.UpgradeNotice = ProfileViewModel.LegacyUpgradeNotice;
        return vm;
    }

    private static ViewModelBase WalletRequest()
    {
        WalletViewModel vm = Wallet();
        vm.RequestAmountText = "0.35";
        vm.RequestDescription = "Invoice 2024-118";
        return vm;
    }

    private static ViewModelBase WalletSendMulti()
    {
        WalletViewModel vm = Wallet();
        vm.SendAddress = "monero:" + FakeAddress('5', 31) + "?tx_amount=0.2&recipient_name=Bob%27s%20Books&tx_description=Order%201043";
        var second = new RecipientRow(2, vm.Profile) { AmountText = "0.05" };
        second.Contact = vm.Profile.Contacts[1];
        vm.ExtraRecipients.Add(second);
        return vm;
    }

    private static ViewModelBase WalletSendConfirmMulti()
    {
        WalletViewModel vm = Wallet();
        vm.ConfirmLines.Add(new ConfirmLine("Bob's Books", FakeAddress('5', 31), "0.2 XMR"));
        vm.ConfirmLines.Add(new ConfirmLine("Carol", FakeAddress('7', 32), "0.05 XMR"));
        vm.ConfirmHasSeveral = true;
        vm.SendSummary = "Default priority · 2 recipients · built and signed, not yet broadcast";
        vm.ConfirmAmountText = "0.25 XMR";
        vm.SendFeeText = "0.00004312 XMR";
        vm.SendTotalText = "0.25004312 XMR";
        vm.ShowSendConfirm = true;
        return vm;
    }

    private static ViewModelBase WalletHistoryDetails()
    {
        WalletViewModel vm = Wallet();
        HistoryRow row = vm.History[1];
        vm.ToggleRowCommand.Execute(row);
        return vm;
    }

    private static ViewModelBase WalletContactEditor()
    {
        WalletViewModel vm = Wallet();
        vm.NewContactCommand.Execute(null);
        vm.ContactName = "Dave";
        vm.ContactAddress = FakeAddress('5', 33);
        vm.ContactNote = "Splits the electricity bill";
        return vm;
    }

    private static ViewModelBase WalletTools()
    {
        WalletViewModel vm = Wallet();
        vm.VerifyTxId = FakeTxId(2);
        vm.VerifyTxKey = FakeTxId(12);
        vm.VerifyAddress = FakeAddress('5', 31);
        vm.VerifyOk = true;
        vm.VerifyResult = "Verified: that address received 1.25 XMR — 24 confirmation(s).";
        vm.SignMessage = "I control this wallet. 2026-10-06";
        vm.Signature = "SigV2" + FakeTxId(13)[..60];
        vm.SignNotice = "Signed with your main address. Share the message, the address and this signature.";
        vm.ReserveAmountText = "10";
        vm.ReserveMessage = "For the landlord, October";
        vm.ReserveProof = "ReserveProofV2" + FakeTxId(14) + FakeTxId(15) + FakeTxId(16);
        vm.ReserveNotice = "Proves at least 10 XMR. Share it with your main address and the message.";
        return vm;
    }

    private static ViewModelBase WalletManage()
    {
        WalletViewModel vm = Wallet();
        vm.OpenManageCommand.Execute(null);
        return vm;
    }

    private static ViewModelBase WalletManageSecrets()
    {
        WalletViewModel vm = Wallet();
        vm.OpenManageCommand.Execute(null);
        vm.RevealedMnemonic = "sober tawny pebbles lunar ought cavernous vixen rally fuming eclipse oars hydrogen vowels " +
                              "nabbing oyster pyramid duke vulture lukewarm tunnel efficient luggage gearbox glass tunnel";
        vm.RevealedViewKey = FakeTxId(17);
        vm.RevealedSpendKey = FakeTxId(18);
        vm.SecretsShown = true;
        vm.RemoveConfirmName = "Everyday";
        vm.RemovePassword = "granite-otter-lantern-41";
        return vm;
    }

    private static ViewModelBase WalletAccounts()
    {
        WalletViewModel vm = Wallet();
        vm.Accounts[0].BalanceText = "12.483017420331 XMR";
        vm.Accounts.Add(new AccountChoice(1, "Business") { BalanceText = "3.2 XMR" });
        return vm;
    }

    private static ViewModelBase WalletWatchOnly()
    {
        WalletProfile profile = DemoProfile();
        profile.ActiveWalletId = profile.Wallets[2].Id;
        return Fill(ProfileViewModel.ForPreview(profile).Start());
    }

    private static AddWalletViewModel AddWalletNew() => new(ProfileViewModel.ForPreview(DemoProfile()));

    private static ViewModelBase AddWalletNewWithSeed()
    {
        AddWalletViewModel vm = AddWalletNew();
        string[] words = ("sober tawny pebbles lunar ought cavernous vixen rally fuming eclipse oars " +
                          "hydrogen vowels nabbing oyster pyramid duke vulture lukewarm tunnel " +
                          "efficient luggage gearbox glass tunnel").Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            vm.SeedWords.Add(new SeedWord(i + 1, words[i]));
        }

        vm.SeedGenerated = true;
        return vm;
    }

    private static ViewModelBase AddWalletKeys()
    {
        AddWalletViewModel vm = AddWalletNew();
        vm.Mode = 3;
        vm.Name = "Shop (watch)";
        vm.ImportAddress = FakeAddress('5', 41);
        vm.ImportViewKey = FakeTxId(19);
        return vm;
    }

    private static ViewModelBase AddWalletConfirm()
    {
        AddWalletViewModel vm = AddWalletNew();
        vm.Mode = 1;
        vm.ImportMnemonic = "sober tawny pebbles lunar ought cavernous vixen rally fuming eclipse oars hydrogen vowels";
        vm.ConfirmAddress = FakeAddress('5', 42);
        vm.ShowConfirm = true;
        return vm;
    }

    private static ViewModelBase WalletSend()
    {
        WalletViewModel vm = Wallet();
        vm.SendAddress = FakeAddress('5', 11);
        vm.SendAmountText = "0,25";
        return vm;
    }

    private static ViewModelBase WalletSent()
    {
        WalletViewModel vm = Wallet();
        vm.SendResult = "Sent 0.25 XMR";
        vm.SendResultDetail = "Network fee 0.00003066 XMR. It confirms in about 2 minutes; " +
                              "your change is spendable again after 10 confirmations (about 20 minutes).";
        vm.SendOutcome = SendOutcome.Sent;
        return vm;
    }

    private static ViewModelBase WalletSendError()
    {
        WalletViewModel vm = Wallet();
        vm.SendAddress = FakeAddress('5', 11);
        vm.SendAmountText = "40";
        vm.SendResult = "Amount exceeds your spendable balance (11.983017420331 XMR). Funds received in the " +
                        "last 10 blocks are still locked.";
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
