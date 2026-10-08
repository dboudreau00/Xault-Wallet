using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
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
