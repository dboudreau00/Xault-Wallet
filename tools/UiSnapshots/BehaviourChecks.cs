using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XaultWallet.Desktop;
using XaultWallet.Desktop.ViewModels;
using XaultWallet.Desktop.Views;

namespace XaultWallet.UiSnapshots;

/// <summary>
/// Behaviour the screenshots can't show, checked through the real views (bindings, input routing)
/// where that is where it went wrong: each of these was a bug found in review.
/// </summary>
internal static partial class Program
{
    private static Window ShowWallet(WalletViewModel vm)
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel(vm), Width = 1080, Height = 760 };
        window.Show();
        Settle();
        return window;
    }

    private static T Find<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>()
            .First(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == automationId);

    private static void Press(TopLevel top, PhysicalKey key)
    {
        top.KeyPressQwerty(key, RawInputModifiers.None);
        top.KeyReleaseQwerty(key, RawInputModifiers.None);
        Settle();
    }

    /// <summary>Let <paramref name="task"/> finish, running the UI thread's work meanwhile (its
    /// continuations need it: blocking on the task here would deadlock).</summary>
    private static void Run(Task task, int seconds = 30)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        if (!task.IsCompleted)
        {
            throw new TimeoutException($"Not finished after {seconds} s.");
        }

        task.GetAwaiter().GetResult();
    }

    private static void Expect(bool ok, string passed, string failed)
    {
        if (ok)
        {
            Console.WriteLine(passed);
        }
        else
        {
            Failures.Add(failed);
        }
    }

    /// <summary>
    /// Choosing a wallet starts its backend. Up/Down on the closed switcher used to step through the
    /// wallets (as a combo box does), starting every one on the way; the mouse wheel too. Now the keys
    /// open the list, the arrows move through it, and only Enter chooses.
    /// </summary>
    private static void CheckSwitcherKeysOpenTheList()
    {
        WalletViewModel vm = Wallet();
        ProfileViewModel profile = vm.Profile;
        Window window = ShowWallet(vm);
        ComboBox switcher = Find<ComboBox>(window, "Wallet.Switcher");
        switcher.Focus();
        Settle();

        window.MouseWheel(switcher.TranslatePoint(new Point(20, 10), window) ?? default, new Vector(0, -1));
        Settle();
        bool wheelIgnored = profile.Active == vm && !switcher.IsDropDownOpen;

        Press(window, PhysicalKey.ArrowDown);
        bool opened = switcher.IsDropDownOpen && profile.Active == vm;
        Press(window, PhysicalKey.ArrowDown);
        Press(window, PhysicalKey.ArrowDown);
        bool browsing = switcher.IsDropDownOpen && profile.Active == vm;
        Press(window, PhysicalKey.Enter);
        string third = profile.Wallets[2].Id;
        string second = profile.Wallets[1].Id;
        bool chose = !switcher.IsDropDownOpen && profile.Active?.WalletId == third;
        bool skipped = !profile.IsOpen(second);
        window.Close();

        Expect(wheelIgnored && opened && browsing && chose && skipped,
            "Switcher ok: the wheel does nothing, Down opens the list, arrows browse it, Enter opens only the chosen wallet.",
            $"Switcher keys: wheel ignored={wheelIgnored}, Down opened={opened}, browsing kept the wallet={browsing}, " +
            $"Enter chose the third={chose}, the second was never started={skipped}");
    }

    /// <summary>
    /// Editing a contact must reach every form paying it — the picker's form in this wallet (whose
    /// selection a list move drops) and another open wallet's — and "Pay" must re-apply the contact's
    /// address even when the form already has that contact picked.
    /// </summary>
    private static void CheckContactEditsReachSendForms()
    {
        WalletViewModel everyday = Wallet();
        ProfileViewModel profile = everyday.Profile;
        Window window = ShowWallet(everyday);
        SelectTab(window, 1);
        Settle();

        ContactRow carol = profile.Contacts.First(c => c.Name == "Carol");
        everyday.SelectedSendContact = carol;
        profile.Switch(profile.Wallets[1].Id);
        WalletViewModel savings = profile.Active!;
        savings.SelectedSendContact = carol;
        var extra = new RecipientRow(2, profile) { Contact = carol };
        savings.ExtraRecipients.Add(extra);
        profile.Switch(everyday.WalletId);
        Settle();

        // Renamed so she sorts last (the list moves her) and given a new address, from the Contacts tab.
        string moved = FakeAddress('7', 77);
        SelectTab(window, 3);
        Settle();
        everyday.EditContactCommand.Execute(carol);
        everyday.ContactName = "Zoe";
        everyday.ContactAddress = moved;
        Run(everyday.SaveContactCommand.ExecuteAsync(null));
        SelectTab(window, 1);
        Settle();

        ComboBox picker = Find<ComboBox>(window, "Send.Contact");
        bool here = everyday.SendAddress == moved && ReferenceEquals(everyday.SelectedSendContact, carol)
            && ReferenceEquals(picker.SelectedItem, carol) && everyday.SendRecipientName == "Contact: Zoe";
        bool there = savings.SendAddress == moved && extra.Address == moved;
        bool sorted = ReferenceEquals(profile.Contacts[^1], carol);

        // Pay again on the contact already picked: the form takes its address as it is now.
        ContactRow bob = profile.Contacts.First(c => c.Name == "Bob's Books");
        everyday.PayContactRowCommand.Execute(bob);
        string fresh = FakeAddress('5', 78);
        bob.Model.Address = fresh;
        everyday.PayContactRowCommand.Execute(bob);
        bool repaid = everyday.SendAddress == fresh;

        // Deleted: the forms keep the address they had, without the name.
        Run(everyday.DeleteContactCommand.ExecuteAsync(carol));
        bool released = savings.SelectedSendContact is null && savings.SendAddress == moved && extra.Contact is null;
        window.Close();

        Expect(here && there && sorted && repaid && released,
            "Contacts ok: an edit reaches the forms paying the contact in every open wallet, Pay re-applies, a delete lets go.",
            $"Contact edits: this wallet's form followed={here} (address {everyday.SendAddress[..8]}…, picker shows " +
            $"{(picker.SelectedItem as ContactRow)?.Name ?? "nothing"}), other wallet followed={there}, list re-sorted={sorted}, " +
            $"Pay re-applied={repaid}, delete released={released}");
    }

    /// <summary>Hiding amounts is one setting: every open wallet and the account picker follow at once.</summary>
    private static void CheckHideAmountsEverywhere()
    {
        WalletViewModel first = Wallet();
        ProfileViewModel profile = first.Profile;
        profile.Switch(profile.Wallets[1].Id);
        WalletViewModel second = profile.Active!;
        profile.Switch(first.WalletId);
        first.Accounts[0].SetBalance(1_500_000_000_000, first.HideBalances);
        bool before = first.HideBalances;

        first.ToggleBalancesCommand.Execute(null);
        bool masked = second.HideBalances && first.Accounts[0].BalanceText == "●●●●●" && second.BalanceDisplay == "●●●●●";
        first.ToggleBalancesCommand.Execute(null);
        bool shown = !second.HideBalances && first.Accounts[0].BalanceText == "1.5 XMR";

        Expect(!before && masked && shown,
            "Hide amounts ok: every open wallet and the account picker follow the toggle.",
            $"Hide amounts: started shown={!before}, all masked={masked}, all shown again={shown} ({first.Accounts[0].BalanceText})");
    }

    /// <summary>A note being typed survives the history list being rebuilt (as it is when a block arrives).</summary>
    private static void CheckNoteDraftSurvivesRebuild()
    {
        WalletViewModel vm = Wallet();
        HistoryRow row = vm.History[1];
        vm.ToggleRowCommand.Execute(row);
        row.NoteDraft = "Half-typed note";
        vm.Height += 1; // a pending row is in the list: this rebuilds it
        HistoryRow again = vm.History.First(r => r.TxId == row.TxId);
        Expect(!ReferenceEquals(again, row) && again.IsExpanded && again.NoteDraft == "Half-typed note",
            "History ok: a rebuild keeps the open row open with the note being typed.",
            $"History rebuild: rebuilt={!ReferenceEquals(again, row)}, open={again.IsExpanded}, draft='{again.NoteDraft}'");
    }

    /// <summary>The Manage sheet opens with the seed and keys hidden, whatever happened before.</summary>
    private static void CheckManageOpensWithSecretsHidden()
    {
        WalletViewModel vm = Wallet();
        vm.RevealedMnemonic = "abbey abbey abbey";
        vm.SecretsShown = true;
        vm.OpenManageCommand.Execute(null);
        Expect(vm.ShowManage && !vm.SecretsShown && vm.RevealedMnemonic.Length == 0,
            "Manage ok: the sheet opens with the seed and keys hidden.",
            $"Manage sheet opened with secrets shown={vm.SecretsShown}, mnemonic kept={vm.RevealedMnemonic.Length > 0}");
    }

    /// <summary>A seed wallet's address, once known, is kept with it: the same wallet added again by its
    /// keys or watch-only is recognised, even in a later session before the seed wallet is opened.</summary>
    private static void CheckSameWalletIsRecognised()
    {
        ProfileViewModel profile = ProfileViewModel.ForPreview(DemoProfile());
        WalletViewModel everyday = profile.Start();
        string address = FakeAddress('5', 7);
        profile.NoteAddress(everyday.WalletId, address); // what opening it does
        var add = new AddWalletViewModel(profile)
        {
            Mode = 3,
            Name = "Everyday (watch)",
            DaemonAddress = "http://127.0.0.1:9", // nothing listens: the import check never gets far
            ImportAddress = address,
            ImportViewKey = new string('a', 64),
        };

        // Should the duplicate go unnoticed, the import check must fail fast, not start a real
        // monero-wallet-rpc found on PATH: point it at one that doesn't exist.
        AppSettings settings = AppServices.Instance.Settings;
        string configured = settings.WalletRpcBinaryPath;
        settings.WalletRpcBinaryPath = Path.Combine(Path.GetTempPath(), "no-such-dir", "monero-wallet-rpc");
        try
        {
            Run(add.AddCommand.ExecuteAsync(null));
        }
        finally
        {
            settings.WalletRpcBinaryPath = configured;
        }

        Expect(add.Error == "That wallet is already in this vault, as “Everyday”." && profile.Wallets.Count == 3,
            "Duplicates ok: a seed wallet added again watch-only is recognised by its recorded address.",
            $"Duplicate wallet: error '{add.Error}', wallets {profile.Wallets.Count}");
    }

    /// <summary>Only account 0's first address is the main address.</summary>
    private static void CheckAccountAddressTitles()
    {
        string main = new AddressRow(0, 0, FakeAddress('5', 1), "", used: false).Title;
        string account = new AddressRow(1, 0, FakeAddress('7', 2), "", used: false).Title;
        string sub = new AddressRow(1, 3, FakeAddress('7', 3), "", used: false).Title;
        Expect(main == "Main address" && account == "Account address" && sub == "Subaddress #3",
            "Addresses ok: another account's first address isn't called the main address.",
            $"Address titles: (0,0) '{main}', (1,0) '{account}', (1,3) '{sub}'");
    }

    /// <summary>A lock (its owner token) or the app's exit ends temporary backend work, and waiting for
    /// it returns once that work has actually ended.</summary>
    private static void CheckTemporaryBackendsEnd()
    {
        var backends = new TemporaryBackends();
        using var owner = new CancellationTokenSource();
        bool ownedEnded = false, exitEnded = false;
        Task<int> owned = backends.RunAsync(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { ownedEnded = true; }
            return 0;
        }, owner.Token);
        Task<int> loose = backends.RunAsync(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { exitEnded = true; }
            return 0;
        });

        owner.Cancel();
        Run(backends.WhenEndedAsync(owner.Token));
        bool lockEndsOwn = ownedEnded && owned.IsCanceled && !loose.IsCompleted;
        Run(backends.StopAllAsync());
        bool exitEndsAll = exitEnded && loose.IsCanceled;
        bool refused = backends.RunAsync(_ => Task.FromResult(1)).IsCanceled;

        Expect(lockEndsOwn && exitEndsAll && refused,
            "Temporary backends ok: a lock ends its own, exit ends the rest, and nothing starts after exit.",
            $"Temporary backends: lock ended its own only={lockEndsOwn}, exit ended the rest={exitEndsAll}, refused after exit={refused}");
    }
}
