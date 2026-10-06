using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>The Tools tab: proofs and signatures — ways to prove something about a payment or a
/// wallet without giving away anything that can spend.</summary>
public sealed partial class WalletViewModel
{
    // ------------------------------------------------------------------ prove / verify a payment

    [ObservableProperty] private string _verifyTxId = string.Empty;
    [ObservableProperty] private string _verifyTxKey = string.Empty;
    [ObservableProperty] private string _verifyAddress = string.Empty;
    [ObservableProperty] private string _verifyResult = string.Empty;
    [ObservableProperty] private bool _verifyOk;

    /// <summary>Copy the last send's txid + tx key into the verify panel as a convenience.</summary>
    [RelayCommand]
    private void PrefillVerifyFromLast()
    {
        VerifyTxId = LastTxId;
        VerifyTxKey = LastTxKey;
        VerifyResult = string.Empty;
    }

    /// <summary>Fetch the tx key for one of THIS wallet's past outgoing transactions, so older
    /// payments can be proven too (only works for transactions this wallet sent).</summary>
    [RelayCommand]
    private async Task FetchTxKeyAsync()
    {
        VerifyResult = string.Empty;
        VerifyOk = false;

        if (!IsReady || string.IsNullOrWhiteSpace(VerifyTxId))
        {
            VerifyResult = "Enter the transaction ID of a payment this wallet sent.";
            return;
        }

        try
        {
            VerifyTxKey = await _wallet.GetTxKeyAsync(VerifyTxId, _cts.Token);
        }
        catch (Exception ex)
        {
            VerifyResult = "Couldn't fetch a key for that transaction (is it one this wallet sent?): " + Friendly(ex);
        }
    }

    [RelayCommand]
    private async Task VerifyPaymentAsync()
    {
        VerifyResult = string.Empty;
        VerifyOk = false;

        if (!IsReady)
        {
            VerifyResult = "Wallet isn't ready yet.";
            return;
        }

        if (string.IsNullOrWhiteSpace(VerifyTxId) || string.IsNullOrWhiteSpace(VerifyTxKey) || string.IsNullOrWhiteSpace(VerifyAddress))
        {
            VerifyResult = "Enter the transaction ID, transaction key, and destination address.";
            return;
        }

        try
        {
            (ulong received, ulong confirmations, bool inPool) =
                await _wallet.CheckTxKeyAsync(VerifyTxId, VerifyTxKey, VerifyAddress, _cts.Token);

            if (received == 0)
            {
                VerifyOk = false;
                VerifyResult = "No payment to that address was found in this transaction.";
            }
            else
            {
                VerifyOk = true;
                string status = inPool ? "in mempool (0 confirmations)" : $"{confirmations:N0} confirmation(s)";
                VerifyResult = $"Verified: that address received {XmrAmount.Format(received)} XMR — {status}.";
            }
        }
        catch (Exception ex)
        {
            VerifyOk = false;
            VerifyResult = "Couldn't verify: " + Friendly(ex);
        }
    }

    [RelayCommand]
    private void CopyVerifyTxKey() => Copy(VerifyTxKey, "Transaction key");

    // ------------------------------------------------------------------ sign / verify a message

    [ObservableProperty] private string _signMessage = string.Empty;
    [ObservableProperty] private string _signature = string.Empty;
    [ObservableProperty] private string _signNotice = string.Empty;

    /// <summary>Sign a message with this wallet's main address: proves you control it.</summary>
    [RelayCommand]
    private async Task SignAsync()
    {
        Signature = string.Empty;
        SignNotice = string.Empty;
        if (!IsReady)
        {
            SignNotice = "Wallet isn't ready yet.";
            return;
        }

        if (!CanSpend)
        {
            SignNotice = "A watch-only wallet has no spend key to sign with.";
            return;
        }

        if (string.IsNullOrEmpty(SignMessage))
        {
            SignNotice = "Type the message to sign.";
            return;
        }

        try
        {
            Signature = await _wallet.SignMessageAsync(SignMessage, _cts.Token);
            SignNotice = "Signed with your main address. Share the message, the address and this signature.";
        }
        catch (Exception ex)
        {
            SignNotice = "Couldn't sign: " + Friendly(ex);
        }
    }

    [RelayCommand]
    private void CopySignature() => Copy(Signature, "Signature");

    [RelayCommand]
    private void CopyPrimaryAddress() => Copy(PrimaryAddress, "Address");

    [ObservableProperty] private string _checkMessage = string.Empty;
    [ObservableProperty] private string _checkMessageAddress = string.Empty;
    [ObservableProperty] private string _checkMessageSignature = string.Empty;
    [ObservableProperty] private string _checkMessageResult = string.Empty;
    [ObservableProperty] private bool _checkMessageOk;

    /// <summary>Check someone's signed message against their address.</summary>
    [RelayCommand]
    private async Task CheckMessageAsync()
    {
        CheckMessageResult = string.Empty;
        CheckMessageOk = false;
        if (!IsReady)
        {
            CheckMessageResult = "Wallet isn't ready yet.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CheckMessageAddress) || string.IsNullOrWhiteSpace(CheckMessageSignature))
        {
            CheckMessageResult = "Enter the message, the address and the signature.";
            return;
        }

        try
        {
            CheckMessageOk = await _wallet.VerifyMessageAsync(CheckMessage, CheckMessageAddress, CheckMessageSignature, _cts.Token);
            CheckMessageResult = CheckMessageOk
                ? "Valid: this message was signed by that address's owner."
                : "NOT valid: the signature doesn't match this message and address.";
        }
        catch (Exception ex)
        {
            CheckMessageResult = "Couldn't check it: " + Friendly(ex);
        }
    }

    // ------------------------------------------------------------------ reserve proof

    [ObservableProperty] private string _reserveAmountText = string.Empty;
    [ObservableProperty] private string _reserveMessage = string.Empty;
    [ObservableProperty] private string _reserveProof = string.Empty;
    [ObservableProperty] private string _reserveNotice = string.Empty;

    /// <summary>Prove this wallet holds at least an amount (blank: everything in the account) without
    /// revealing its addresses' history. Anyone with the proof learns the proven total.</summary>
    [RelayCommand]
    private async Task CreateReserveProofAsync()
    {
        ReserveProof = string.Empty;
        ReserveNotice = string.Empty;
        if (!IsReady)
        {
            ReserveNotice = "Wallet isn't ready yet.";
            return;
        }

        if (!CanSpend)
        {
            ReserveNotice = "A watch-only wallet can't make a reserve proof: it needs the spend key.";
            return;
        }

        decimal? amount = null;
        if (ReserveAmountText.Trim().Length > 0)
        {
            if (!XmrAmount.TryParse(ReserveAmountText, out decimal a, out string? error) || a <= 0m)
            {
                ReserveNotice = error ?? "Enter an amount, or leave it empty to prove the whole account.";
                return;
            }

            amount = a;
        }

        try
        {
            ReserveProof = await _wallet.GetReserveProofAsync(amount, AccountIndex, ReserveMessage, _cts.Token);
            ReserveNotice = amount is { } x
                ? $"Proves at least {XmrAmount.Format(x)} XMR. Share it with your main address and the message."
                : "Proves everything in this account. Share it with your main address and the message.";
        }
        catch (Exception ex)
        {
            ReserveNotice = "Couldn't make the proof: " + Friendly(ex);
        }
    }

    [RelayCommand]
    private void CopyReserveProof() => Copy(ReserveProof, "Reserve proof");

    [ObservableProperty] private string _checkReserveAddress = string.Empty;
    [ObservableProperty] private string _checkReserveMessage = string.Empty;
    [ObservableProperty] private string _checkReserveProof = string.Empty;
    [ObservableProperty] private string _checkReserveResult = string.Empty;
    [ObservableProperty] private bool _checkReserveOk;

    [RelayCommand]
    private async Task CheckReserveProofAsync()
    {
        CheckReserveResult = string.Empty;
        CheckReserveOk = false;
        if (!IsReady)
        {
            CheckReserveResult = "Wallet isn't ready yet.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CheckReserveAddress) || string.IsNullOrWhiteSpace(CheckReserveProof))
        {
            CheckReserveResult = "Enter the address, the message (if any) and the proof.";
            return;
        }

        try
        {
            (bool good, decimal total, decimal spent) = await _wallet.CheckReserveProofAsync(CheckReserveAddress, CheckReserveMessage, CheckReserveProof, _cts.Token);
            CheckReserveOk = good && spent == 0m;
            CheckReserveResult = !good
                ? "NOT valid: the proof doesn't match this address and message."
                : spent == 0m
                    ? $"Valid: that wallet holds {XmrAmount.Format(total)} XMR, none of it spent."
                    : $"The proof is valid, but {XmrAmount.Format(spent)} of the {XmrAmount.Format(total)} XMR it covers has been SPENT since.";
        }
        catch (Exception ex)
        {
            CheckReserveResult = "Couldn't check it: " + Friendly(ex);
        }
    }

    // ------------------------------------------------------------------ maintenance

    [ObservableProperty] private string _rescanNotice = string.Empty;

    /// <summary>Re-check which outputs are spent: fixes a balance confused by a bad node.</summary>
    [RelayCommand]
    private async Task RescanSpentAsync()
    {
        if (!IsReady)
        {
            return;
        }

        RescanNotice = "Checking which outputs are spent…";
        try
        {
            await _wallet.RescanSpentAsync(_cts.Token);
            RescanNotice = "Done: spent outputs were re-checked against the node.";
            await SoftRefreshAsync();
        }
        catch (OperationCanceledException)
        {
            RescanNotice = string.Empty;
        }
        catch (Exception ex)
        {
            RescanNotice = "Couldn't re-check: " + Friendly(ex);
        }
    }
}
