using Avalonia.Controls;
using XaultWallet.Core.Companion;
using XaultWallet.Core.Monero;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.UiSnapshots;

/// <summary>Screens of the browser companion and Enhanced Security.</summary>
internal static partial class Program
{
    private static MainWindowViewModel Shell(Window w) => (MainWindowViewModel)w.DataContext!;

    private static string RequestLink(char prefix) =>
        MoneroUri.Build(new MoneroPaymentRequest(FakeAddress(prefix, 21), 1.5m, "Order 4471", "Shop Example"));

    /// <summary>Settings after connecting Chrome (in the throwaway profile: a registry in memory on
    /// Windows, a temporary home elsewhere).</summary>
    private static ViewModelBase SettingsCompanion()
    {
        var vm = new SettingsViewModel();
        vm.ConnectBrowserCommand.Execute("chrome");
        return vm;
    }

    private static ViewModelBase UnlockWithRequest() => new UnlockViewModel
    {
        PendingRequestNote = "A payment request from your browser is waiting. Unlock to review it in the Send tab.",
    };

    /// <param name="settled">Shown a moment later, Review no longer held (as a screenshot shows it).</param>
    private static WalletViewModel WalletBrowserRequest(char prefix, bool settled = true)
    {
        WalletViewModel vm = Wallet();
        if (vm.ApplyPaymentRequest(new OutsidePayment(RequestLink(prefix), CompanionCaller.Chrome, "https://shop.example")) is not PaymentOffer.Shown and var refused)
        {
            Failures.Add("The browser request was refused: " + refused);
        }

        vm.ReviewHeld = !settled && vm.ReviewHeld;
        return vm;
    }

    private static ViewModelBase WalletConfirmRequest()
    {
        WalletViewModel vm = WalletBrowserRequest('5');
        vm.ConfirmSendAddress = FakeAddress('5', 21);
        vm.ConfirmFromText = "From Everyday";
        vm.ConfirmRequestSource = "Came from outside XaultWallet: your browser (shop.example). XaultWallet can't confirm who sent it; check the address.";
        vm.SendSummary = "Default priority · built and signed, not yet broadcast";
        vm.ConfirmAmountText = "1.5 XMR";
        vm.SendFeeText = "0.00003066 XMR";
        vm.SendTotalText = "1.50003066 XMR";
        vm.ShowSendConfirm = true;
        vm.ConfirmHeld = false; // as it looks a moment later
        return vm;
    }

    private static void ShowTripwire(Window w)
    {
        SelectTab(w, 0);
        Shell(w).ShowNotice("Your Chrome connection to XaultWallet has been changed and no longer leads to this app. " +
                            "If you didn't do that, another program may be trying to stand in for XaultWallet. Review it in Settings.",
            warning: true, offersSettings: true);
    }

    /// <summary>A browser request that came while the user was filling the Send form: it waits behind Open it.</summary>
    private static void ShowHeldRequest(Window w)
    {
        SelectTab(w, 1);
        Shell(w).HeldRequestNote = "A payment request from your browser (shop.example) is waiting, because the Send form holds a payment you started. " +
                                   "Open it to put it in the Send form in place of that.";
    }

    /// <summary>A browser asked for a new receive address: the app says which site and waits for Give address.</summary>
    private static void ShowReceiveAsk(Window w)
    {
        SelectTab(w, 0);
        Shell(w).ReceiveAskNote = "Your browser asks for a new receive address for shop.example. " +
                                  "Give it one, then ask again in the browser within two minutes. It will be labelled just \"Browser\".";
    }

    private static void ShowEnhancedConfirm(Window w)
    {
        Shell(w).ConfirmingEnhancedSecurity = true;
        Shell(w).EnhancedSecurityCountdown = $"Enhanced Security is on. Click Keep it on within {MainWindowViewModel.EnhancedSecurityConfirmSeconds} s to confirm XaultWallet still gets your clicks, or it turns itself off.";
        ScrollToNamed(w, "Settings.EnhancedCard", above: 40);
    }

    /// <summary>The browser request lands in the Send tab with its source; one for another network says
    /// so at once.</summary>
    private static void CheckBrowserRequestLandsInSend()
    {
        WalletViewModel vm = WalletBrowserRequest('5');
        Expect(vm.SelectedTab == 1, "a browser request opens the Send tab");
        Expect(vm.SendAddress == FakeAddress('5', 21), "the request's address is in the form");
        Expect(vm.SendAmountText == "1.5", "the request's amount is in the form, not " + vm.SendAmountText);
        Expect(vm.PaymentRequestNote.StartsWith("Opened from your browser (shop.example).", StringComparison.Ordinal)
               && vm.PaymentRequestNote.Contains("“Shop Example: Order 4471”", StringComparison.Ordinal) && !vm.PaymentRequestIsProblem,
            "the note says where from first, then what, quoted: " + vm.PaymentRequestNote);

        WalletViewModel wrong = WalletBrowserRequest('4');
        Expect(wrong.PaymentRequestIsProblem && wrong.PaymentRequestNote.Contains("Stagenet", StringComparison.Ordinal),
            "a mainnet request in a stagenet wallet is flagged at once: " + wrong.PaymentRequestNote);

        // A link pasted by hand afterwards is not "from the browser".
        vm.SendAddress = MoneroUri.Build(new MoneroPaymentRequest(FakeAddress('5', 22), 2m));
        Expect(!vm.PaymentRequestNote.Contains("browser", StringComparison.Ordinal), "a pasted link keeps no browser source: " + vm.PaymentRequestNote);

        // Review waits a moment after a request lands, so a click meant for something else can't land on it.
        WalletViewModel held = WalletBrowserRequest('5', settled: false);
        Expect(held.ReviewHeld && !held.CanReview, "Review is held right after a request fills the form");

        // Never under an open confirmation, over the user's own form, over a payment to check, or into a watch-only wallet.
        var other = new OutsidePayment(RequestLink('5'), CompanionCaller.Firefox, string.Empty);
        WalletViewModel confirming = Wallet();
        confirming.ShowSendConfirm = true;
        Expect(confirming.ApplyPaymentRequest(other) == PaymentOffer.Confirming, "a request never replaces a payment being confirmed");

        WalletViewModel typed = Wallet();
        typed.SendAddress = FakeAddress('5', 30);
        Expect(typed.ApplyPaymentRequest(other) == PaymentOffer.FormInUse && typed.SendAddress == FakeAddress('5', 30),
            "a request never replaces an address the user typed");

        WalletViewModel edited = WalletBrowserRequest('5');
        edited.SendAmountText = "2";
        Expect(edited.ApplyPaymentRequest(other) == PaymentOffer.FormInUse && edited.SendAmountText == "2",
            "a request never replaces a request whose amount the user changed");

        WalletViewModel untouched = WalletBrowserRequest('5');
        Expect(untouched.ApplyPaymentRequest(other) == PaymentOffer.Shown && untouched.PaymentRequestNote.Contains("replaced an earlier request", StringComparison.Ordinal),
            "an untouched request is replaced, and the note says so: " + untouched.PaymentRequestNote);

        WalletViewModel check = Wallet();
        check.SendResult = "The payment may have been sent.";
        check.SendOutcome = SendOutcome.NeedsCheck;
        Expect(check.ApplyPaymentRequest(other) == PaymentOffer.CheckLastPayment && check.SendNeedsCheck,
            "a request never hides a payment that may have gone out");

        // A note can't fake a line or turn the warning around.
        string sneaky = "monero:" + FakeAddress('5', 21) + "?tx_description=Rent%0AOpened%20from%20your%20bank%E2%80%AE";
        WalletViewModel spoof = Wallet();
        spoof.ApplyPaymentRequest(new OutsidePayment(sneaky, CompanionCaller.Chrome, "http://shop.example"));
        Expect(spoof.PaymentRequestNote.Split('\n').Length == 2 && spoof.PaymentRequestNote.Contains("“Rent Opened from your bank”", StringComparison.Ordinal)
               && spoof.PaymentRequestNote.Contains("(http://shop.example, not encrypted)", StringComparison.Ordinal),
            "a request's note is cleaned and a plain http page is marked: " + spoof.PaymentRequestNote);
        Console.WriteLine("Companion ok: a browser request fills Send with its source and holds Review; a wrong network is flagged; " +
                          "a confirmation, the user's own form or a payment to check is never replaced.");
    }

    private static void Expect(bool ok, string what)
    {
        if (!ok)
        {
            Failures.Add("Companion: " + what);
        }
    }
}
