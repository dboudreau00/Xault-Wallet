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
        PendingRequestNote = "A payment request from your browser (shop.example) is waiting. Unlock to review it in the Send tab.",
    };

    private static WalletViewModel WalletBrowserRequest(char prefix)
    {
        WalletViewModel vm = Wallet();
        if (vm.ApplyPaymentRequest(new OutsidePayment(RequestLink(prefix), CompanionCaller.Chrome, "https://shop.example")) is { } refused)
        {
            Failures.Add("The browser request was refused: " + refused);
        }

        return vm;
    }

    private static ViewModelBase WalletConfirmRequest()
    {
        WalletViewModel vm = WalletBrowserRequest('5');
        vm.ConfirmSendAddress = FakeAddress('5', 21);
        vm.ConfirmFromText = "From Everyday";
        vm.ConfirmRequestSource = "Requested by your browser (shop.example)";
        vm.SendSummary = "Default priority · built and signed, not yet broadcast";
        vm.ConfirmAmountText = "1.5 XMR";
        vm.SendFeeText = "0.00003066 XMR";
        vm.SendTotalText = "1.50003066 XMR";
        vm.ShowSendConfirm = true;
        return vm;
    }

    private static void ShowTripwire(Window w)
    {
        SelectTab(w, 0);
        Shell(w).ShowNotice("Your Chrome connection to XaultWallet has been changed and no longer leads to this app. " +
                            "If you didn't do that, another program may be trying to stand in for XaultWallet. Review it in Settings.",
            warning: true, offersSettings: true);
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
        Expect(vm.PaymentRequestNote.Contains("from Shop Example: Order 4471", StringComparison.Ordinal)
               && vm.PaymentRequestNote.Contains("your browser (shop.example)", StringComparison.Ordinal) && !vm.PaymentRequestIsProblem,
            "the note says what and where from: " + vm.PaymentRequestNote);

        WalletViewModel wrong = WalletBrowserRequest('4');
        Expect(wrong.PaymentRequestIsProblem && wrong.PaymentRequestNote.Contains("Stagenet", StringComparison.Ordinal),
            "a mainnet request in a stagenet wallet is flagged at once: " + wrong.PaymentRequestNote);

        // A link pasted by hand afterwards is not "from the browser".
        vm.SendAddress = MoneroUri.Build(new MoneroPaymentRequest(FakeAddress('5', 22), 2m));
        Expect(!vm.PaymentRequestNote.Contains("browser", StringComparison.Ordinal), "a pasted link keeps no browser source: " + vm.PaymentRequestNote);

        // Never under an open confirmation.
        WalletViewModel confirming = Wallet();
        confirming.ShowSendConfirm = true;
        Expect(confirming.ApplyPaymentRequest(new OutsidePayment(RequestLink('5'), CompanionCaller.Firefox, string.Empty)) is not null,
            "a request never replaces a payment being confirmed");
        Console.WriteLine("Companion ok: a browser request fills Send with its source, a wrong network is flagged, a confirmation is never replaced.");
    }

    private static void Expect(bool ok, string what)
    {
        if (!ok)
        {
            Failures.Add("Companion: " + what);
        }
    }
}
