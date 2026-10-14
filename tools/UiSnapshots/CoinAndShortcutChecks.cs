using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using XaultWallet.Desktop;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.UiSnapshots;

internal static partial class Program
{
    /// <summary>
    /// Coin control: the vault's frozen list decides what shows as frozen (wallet-rpc's own flag
    /// lags until the freeze is re-applied after an open), the frozen total reaches the balance
    /// card, Hide amounts masks coins too, and a watch-only wallet offers no freeze at all.
    /// </summary>
    private static void CheckCoinControl()
    {
        WalletViewModel vm = Wallet();
        string frozen = FakeKeyImage(41);
        vm.FreezeForPreview(frozen);
        List<OwnedOutput> coins = DemoCoins(frozen);
        coins[0].Frozen = false; // as a fresh backend reports it, before the re-freeze
        vm.SetCoins(coins);
        bool marked = vm.Coins[0].IsFrozen && vm.Coins.Skip(1).All(c => !c.IsFrozen);
        bool total = vm.HasFrozen && vm.FrozenDisplay == "Frozen 8 XMR";
        bool summary = vm.CoinsSummary.StartsWith("4 coins · 1 frozen", StringComparison.Ordinal);

        vm.Profile.SetHideBalances(true);
        bool masked = vm.Coins.All(c => !c.AmountText.Contains("XMR", StringComparison.Ordinal))
                      && !vm.FrozenDisplay.Contains("8 XMR", StringComparison.Ordinal)
                      && !vm.CoinsSummary.Contains("XMR", StringComparison.Ordinal);
        vm.Profile.SetHideBalances(false);

        WalletViewModel watch = WalletViewModel.ForPreview(new WalletSecrets { Network = MoneroNetwork.Stagenet, Kind = WalletKind.ViewOnly, Address = FakeAddress('5', 22) });
        watch.SetCoins([new OwnedOutput { Amount = 1, KeyImage = string.Empty, TxHash = FakeTxId(7), BlockHeight = 10, Unlocked = true }]);
        bool watchOnly = watch.Coins.Count == 1 && !watch.Coins[0].CanFreeze;

        Expect(marked && total && summary && masked && watchOnly,
            "Coins ok: the vault's frozen list marks coins, the total reaches the balance card, Hide amounts masks coins, watch-only can't freeze.",
            $"Coin control: marked={marked}, total={total} ('{vm.FrozenDisplay}'), summary={summary} ('{vm.CoinsSummary}'), " +
            $"masked={masked}, watch-only offers no freeze={watchOnly}");
    }

    /// <summary>Ctrl (Cmd on macOS) + 1…6 picks the tabs and + H hides amounts, from wherever focus is.</summary>
    private static void CheckShortcutsPickTabs()
    {
        WalletViewModel vm = Wallet();
        Window window = ShowWallet(vm);
        window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(); // even from a text box
        Settle();
        RawInputModifiers command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        void Chord(PhysicalKey key)
        {
            window.KeyPressQwerty(key, command);
            window.KeyReleaseQwerty(key, command);
            Settle();
        }

        Chord(PhysicalKey.Digit6);
        bool coins = vm.SelectedTab == 5;
        Chord(PhysicalKey.Digit2);
        bool send = vm.SelectedTab == 1;
        bool shown = !vm.HideBalances;
        Chord(PhysicalKey.H);
        bool hidden = vm.HideBalances;
        Chord(PhysicalKey.H);
        vm.Profile.SetHideBalances(false);
        window.Close();

        Expect(coins && send && shown && hidden,
            "Shortcuts ok: Ctrl+6 opens Coins, Ctrl+2 Send, Ctrl+H hides amounts, even from a text box.",
            $"Shortcuts: Ctrl+6 coins={coins}, Ctrl+2 send={send}, Ctrl+H hid amounts={hidden} (were shown={shown})");
    }

    /// <summary>No key press sends a payment: on the confirmation, Send now never takes the keyboard focus,
    /// so Tab can't reach it and Enter or Space can't press it. Only a click can.</summary>
    private static void CheckSendNowTakesAClick()
    {
        WalletViewModel vm = Wallet();
        vm.SelectedTab = 1;
        vm.ConfirmSendAddress = FakeAddress('5', 21);
        vm.ConfirmAmountText = "1.5 XMR";
        vm.ShowSendConfirm = true;
        vm.ConfirmHeld = false;
        Window window = ShowWallet(vm);
        Settle();
        Button? send = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetAutomationId(b) == "SendConfirm.Send");
        bool reached = false;
        for (int i = 0; i < 80 && send is not null && !reached; i++)
        {
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Settle();
            reached = send.IsFocused;
        }

        window.Close();
        Expect(send is { Focusable: false, IsTabStop: false } && !reached,
            "Send now ok: it never takes the keyboard focus, so no key press can send.",
            $"Send now: found={send is not null}, focusable={send?.Focusable}, tab stop={send?.IsTabStop}, reached by Tab={reached}");
    }

    /// <summary>
    /// A running wallet-rpc keeps how it reaches nodes, so a node that needs the other way restarts the wallet's
    /// backend instead of being switched in live: with the built-in Tor, a wallet opened on a node on this computer
    /// (no proxy) must not be given a remote node directly, and the reverse; with a proxy of the user's own or no
    /// proxy at all nothing changes with the node.
    /// </summary>
    private static void CheckNodeSwitchRoutes()
    {
        AppSettings settings = AppServices.Instance.Settings;
        (bool tor, string proxy) = (settings.UseBuiltInTor, settings.ProxyAddress);
        const string local = "http://127.0.0.1:18081", remote = "http://node.example.org:18089";
        bool torOk, proxyOk, directOk;
        try
        {
            settings.UseBuiltInTor = true;
            settings.ProxyAddress = string.Empty;
            string toLocal = AppServices.Instance.DescribeRoute(local), toRemote = AppServices.Instance.DescribeRoute(remote);
            torOk = toLocal == "Local node" && toRemote == "Tor"
                && WalletViewModel.NodeSwitchNeedsRestart(backendBehindProxy: false, toRemote)
                && WalletViewModel.NodeSwitchNeedsRestart(backendBehindProxy: true, toLocal)
                && !WalletViewModel.NodeSwitchNeedsRestart(backendBehindProxy: true, toRemote)
                && !WalletViewModel.NodeSwitchNeedsRestart(backendBehindProxy: false, toLocal);

            settings.UseBuiltInTor = false;
            settings.ProxyAddress = "127.0.0.1:9050";
            proxyOk = AppServices.Instance.DescribeRoute(local) == "SOCKS proxy" && AppServices.Instance.DescribeRoute(remote) == "SOCKS proxy"
                && !WalletViewModel.NodeSwitchNeedsRestart(backendBehindProxy: true, "SOCKS proxy");

            settings.ProxyAddress = string.Empty;
            directOk = AppServices.Instance.DescribeRoute(local) == "Direct" && AppServices.Instance.DescribeRoute(remote) == "Direct"
                && !WalletViewModel.NodeSwitchNeedsRestart(backendBehindProxy: false, "Direct");
        }
        finally
        {
            (settings.UseBuiltInTor, settings.ProxyAddress) = (tor, proxy);
        }

        Expect(torOk && proxyOk && directOk,
            "Node switch ok: with Tor on, a node reached the other way (local or remote) restarts the backend; otherwise it switches live.",
            $"Node switch routes: tor={torOk}, own proxy={proxyOk}, direct={directOk}");
    }

    /// <summary>Scroll the control with this automation id into view, with some room above it.</summary>
    private static void ScrollToNamed(Window w, string automationId, double above = 330)
    {
        Control? target = w.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == automationId);
        ScrollViewer? sv = target?.FindAncestorOfType<ScrollViewer>();
        if (target is null || sv?.Content is not Visual content || target.TranslatePoint(new Point(0, 0), content) is not { } p)
        {
            Failures.Add($"Nothing named {automationId} to scroll to");
            return;
        }

        sv.Offset = new Vector(0, Math.Max(0, p.Y - above));
    }
}
