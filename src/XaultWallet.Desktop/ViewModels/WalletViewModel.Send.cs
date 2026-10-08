using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>An additional recipient of the same transaction (the first one is the main form).</summary>
public sealed partial class RecipientRow : ObservableObject
{
    public RecipientRow(int number, ProfileViewModel profile)
    {
        Number = number;
        Profile = profile;
    }

    /// <summary>For the contact picker (bound from the row itself, so it never waits on the tree).</summary>
    public ProfileViewModel Profile { get; }

    /// <summary>2, 3, … (the main form is recipient 1).</summary>
    public int Number { get; set; }

    public string Title => $"Recipient {Number}";

    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _amountText = string.Empty;
    [ObservableProperty] private string _amountPreview = string.Empty;
    [ObservableProperty] private bool _amountPreviewIsError;

    /// <summary>Picked from the address book (fills the address).</summary>
    [ObservableProperty] private ContactRow? _contact;

    /// <summary>The contact this row pays: picked, its address not edited since. Kept apart from the
    /// picker's selection, which a picker drops when the contact list moves the item.</summary>
    private ContactRow? _payee;

    partial void OnContactChanged(ContactRow? value)
    {
        if (value is not null)
        {
            _payee = value;
            Address = value.Address;
        }
    }

    partial void OnAddressChanged(string value)
    {
        if (_payee is { } payee && payee.Address != value.Trim())
        {
            _payee = null; // edited away from the picked contact
            Contact = null;
        }
    }

    /// <summary>A contact was edited or deleted: a row paying it follows (or lets it go).</summary>
    internal void OnContactChanged(ContactRow row, bool removed)
    {
        if (!ReferenceEquals(_payee, row))
        {
            return;
        }

        if (removed)
        {
            _payee = null; // the address stays as it was
            Contact = null;
        }
        else
        {
            Address = row.Address;
            Contact = row;
        }
    }

    partial void OnAmountTextChanged(string value)
    {
        (AmountPreview, AmountPreviewIsError) = WalletViewModel.PreviewAmount(value);
    }

    internal void Renumber(int number)
    {
        Number = number;
        OnPropertyChanged(nameof(Number));
        OnPropertyChanged(nameof(Title));
    }
}

/// <summary>One line of the send confirmation: who gets how much.</summary>
public sealed record ConfirmLine(string Who, string Address, string Amount);

public sealed partial class WalletViewModel
{
    // ------------------------------------------------------------------ the form

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendRecipientName))]
    private string _sendAddress = string.Empty;

    /// <summary>The amount exactly as typed. Parsed by <see cref="XmrAmount"/> (culture-independent)
    /// — never bound as a number, because culture-aware conversion turned "0,25" into 25 XMR.</summary>
    [ObservableProperty] private string _sendAmountText = string.Empty;

    /// <summary>Live read-back of how the typed amount is understood, e.g. "= 0.25 XMR".</summary>
    [ObservableProperty] private string _sendAmountPreview = string.Empty;
    [ObservableProperty] private bool _sendAmountPreviewIsError;

    /// <summary>What a pasted payment link asked for ("Payment request: Rent July"), or why it can't be paid.</summary>
    [ObservableProperty] private string _paymentRequestNote = string.Empty;
    [ObservableProperty] private bool _paymentRequestIsProblem;

    /// <summary>The address the shown payment request is for: the note goes when the address changes.</summary>
    private string _paymentRequestAddress = string.Empty;

    /// <summary>Picked from the address book: fills the address.</summary>
    [ObservableProperty] private ContactRow? _selectedSendContact;

    /// <summary>The contact the form pays: picked, its address not edited since. Kept apart from the
    /// picker's selection, which a picker drops when the contact list moves the item.</summary>
    private ContactRow? _payee;

    /// <summary>Contact addresses replaced since the payment being built was snapshotted: a payment
    /// to one of them is set aside when it is ready, as it is once the confirmation shows.</summary>
    private readonly HashSet<string> _replacedAddresses = new(StringComparer.Ordinal);

    private const string ReplacedWhileBuilding =
        "A contact's address changed while this payment was being built, so nothing was sent. Review it again.";

    /// <summary>The contact the typed address belongs to, if any.</summary>
    public string SendRecipientName => _profile.ContactNameFor(SendAddress) is { } name ? "Contact: " + name : string.Empty;

    partial void OnSelectedSendContactChanged(ContactRow? value)
    {
        if (value is not null)
        {
            _payee = value;
            SendAddress = value.Address;
        }
    }

    /// <summary>A contact was edited or deleted, from any wallet's Contacts tab. A form paying it
    /// follows: to the new address, or (deleted) it keeps the address and drops the name. A payment
    /// already built for its old address is set aside, to be reviewed again.</summary>
    internal void OnContactChanged(ContactRow row, string oldAddress, bool removed)
    {
        if (removed && ReferenceEquals(_editingContact, row))
        {
            CancelContact(); // deleted from another wallet's Contacts tab while being edited here
        }

        if (ReferenceEquals(_payee, row))
        {
            if (removed)
            {
                _payee = null;
                SelectedSendContact = null;
            }
            else
            {
                SendAddress = row.Address;
                SelectedSendContact = row; // back in the picker if the list move dropped it
            }
        }

        foreach (RecipientRow r in ExtraRecipients)
        {
            r.OnContactChanged(row, removed);
        }

        if (!removed && oldAddress != row.Address)
        {
            _replacedAddresses.Add(oldAddress); // a payment still being built to it is set aside when ready
            if (ShowSendConfirm && ConfirmLines.Any(l => l.Address == oldAddress))
            {
                CancelSend();
                SendResult = $"{row.Name}'s address changed, so nothing was sent. Review the payment again.";
            }
        }

        OnContactNamesChanged();
    }

    /// <summary>The address book changed: names shown for addresses are redrawn.</summary>
    internal void OnContactNamesChanged()
    {
        OnPropertyChanged(nameof(SendRecipientName));
        RebuildHistoryRows(); // destinations show contact names
    }

    partial void OnSendAddressChanged(string value)
    {
        // A pasted monero: link fills the form (amount included) instead of being sent as an "address".
        if (MoneroUri.IsUri(value))
        {
            MoneroPaymentRequest? request = MoneroUri.TryParse(value, out string? problem);
            if (request is null)
            {
                PaymentRequestNote = problem ?? "That payment link can't be used.";
                PaymentRequestIsProblem = true;
                return;
            }

            _paymentRequestAddress = request.Address;
            SendAddress = request.Address; // re-enters this handler with a plain address
            if (request.Amount is { } amount)
            {
                SendAmountText = XmrAmount.Format(amount);
            }

            string who = request.RecipientName.Length > 0 ? " from " + request.RecipientName : string.Empty;
            string what = request.Description.Length > 0 ? ": " + request.Description : string.Empty;
            PaymentRequestNote = $"Payment request{who}{what}";
            PaymentRequestIsProblem = false;
            return;
        }

        if (_payee is { } payee && payee.Address != value.Trim())
        {
            _payee = null; // edited away from the picked contact
            SelectedSendContact = null;
        }

        if (PaymentRequestNote.Length > 0 && value.Trim() != _paymentRequestAddress)
        {
            PaymentRequestNote = string.Empty; // the request was for another address
            PaymentRequestIsProblem = false;
            _paymentRequestAddress = string.Empty;
        }
    }

    partial void OnSendAmountTextChanged(string value) =>
        (SendAmountPreview, SendAmountPreviewIsError) = PreviewAmount(value);

    /// <summary>"= 0.25 XMR", or why the text isn't an amount.</summary>
    internal static (string preview, bool isError) PreviewAmount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (string.Empty, false);
        }

        if (XmrAmount.TryParse(value, out decimal xmr, out string? error))
        {
            return (XmrAmount.LooksThousandsGrouped(value)
                ? $"= {XmrAmount.Format(xmr)} XMR — the separator is read as a decimal point"
                : $"= {XmrAmount.Format(xmr)} XMR", false);
        }

        return (error ?? "Invalid amount.", true);
    }

    [ObservableProperty] private int _sendPriority = 1;

    /// <summary>More recipients in the same transaction (one fee for all of them).</summary>
    public ObservableCollection<RecipientRow> ExtraRecipients { get; } = new();

    public bool HasExtraRecipients => ExtraRecipients.Count > 0;

    [RelayCommand]
    private void AddRecipient()
    {
        if (ExtraRecipients.Count + 1 >= MoneroWalletService.MaxDestinations)
        {
            SendResult = $"One transaction pays at most {MoneroWalletService.MaxDestinations} recipients.";
            return;
        }

        ExtraRecipients.Add(new RecipientRow(ExtraRecipients.Count + 2, _profile));
        OnPropertyChanged(nameof(HasExtraRecipients));
    }

    [RelayCommand]
    private void RemoveRecipient(RecipientRow? row)
    {
        if (row is null || !ExtraRecipients.Remove(row))
        {
            return;
        }

        for (int i = 0; i < ExtraRecipients.Count; i++)
        {
            ExtraRecipients[i].Renumber(i + 2);
        }

        OnPropertyChanged(nameof(HasExtraRecipients));
    }

    // ------------------------------------------------------------------ outcome

    /// <summary>One line on the outcome of the last send attempt ("Sent 12.5 XMR", or why not).</summary>
    [ObservableProperty] private string _sendResult = string.Empty;

    /// <summary>Second line for a completed send: the fee and when the money moves.</summary>
    [ObservableProperty] private string _sendResultDetail = string.Empty;

    /// <summary>How <see cref="SendResult"/> is presented: success, "check before retrying", or not sent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendSucceeded))]
    [NotifyPropertyChangedFor(nameof(SendNeedsCheck))]
    [NotifyPropertyChangedFor(nameof(SendFailed))]
    private SendOutcome _sendOutcome;

    public bool SendSucceeded => SendOutcome == SendOutcome.Sent;

    public bool SendNeedsCheck => SendOutcome == SendOutcome.NeedsCheck;

    public bool SendFailed => SendOutcome == SendOutcome.Failed;

    // Any message is a "not sent" until a send path says otherwise; clearing it clears the outcome.
    partial void OnSendResultChanged(string value)
    {
        if (value.Length == 0)
        {
            SendOutcome = SendOutcome.None;
            SendResultDetail = string.Empty;
            OfferSaveRecipient = false;
        }
        else if (SendOutcome == SendOutcome.None)
        {
            SendOutcome = SendOutcome.Failed;
        }
    }

    [ObservableProperty] private bool _sending;

    /// <summary>After paying an address that isn't in the address book: offer to save it.</summary>
    [ObservableProperty] private bool _offerSaveRecipient;
    private string _lastRecipientAddress = string.Empty;

    [RelayCommand]
    private void SaveRecipientAsContact()
    {
        OfferSaveRecipient = false;
        StartNewContact(_lastRecipientAddress);
        SelectedTab = 3;
    }

    // ------------------------------------------------------------------ confirmation

    // Send confirmation overlay (irreversible action — always confirm)
    [ObservableProperty] private bool _showSendConfirm;
    [ObservableProperty] private string _sendSummary = string.Empty;
    [ObservableProperty] private string _confirmAmountText = string.Empty;
    [ObservableProperty] private string _sendFeeText = string.Empty;
    [ObservableProperty] private string _sendTotalText = string.Empty;

    /// <summary>Snapshot of the destination the prepared tx actually pays (single recipient). The
    /// overlay binds to THIS, not the live SendAddress field — so nothing typed under the overlay can
    /// make the display disagree with what Confirm broadcasts.</summary>
    [ObservableProperty] private string _confirmSendAddress = string.Empty;

    /// <summary>The single recipient's contact name, when it is a contact.</summary>
    [ObservableProperty] private string _confirmRecipientName = string.Empty;

    /// <summary>Snapshot of every recipient, for a transaction that pays several.</summary>
    public ObservableCollection<ConfirmLine> ConfirmLines { get; } = new();

    [ObservableProperty] private bool _confirmHasSeveral;

    /// <summary>Set when the prepared fee is anomalously high relative to the amount —
    /// a habituated user shouldn't be able to click through a fee spike unwarned.</summary>
    [ObservableProperty] private string _sendFeeWarning = string.Empty;

    // Transaction built by ReviewSend (do_not_relay) and broadcast only on explicit confirm.
    // Holds the exact fee; discarding it (Cancel) means nothing ever touches the network.
    private TransferResult? _preparedTx;
    private decimal _preparedAmount;

    // Prepared sweep-all (Send max): same do_not_relay contract, but may span several
    // transactions. Mutually exclusive with _preparedTx — exactly one is non-null while the
    // confirm overlay is up.
    private SweepAllResult? _preparedSweep;

    // Payment proof (the tx key from the most recent send — safe to share for explorer verification)
    [ObservableProperty] private bool _hasLastTx;
    [ObservableProperty] private string _lastTxId = string.Empty;
    [ObservableProperty] private string _lastTxKey = string.Empty;

    [RelayCommand]
    private void CopyLastTxId() => Copy(LastTxId, "Transaction ID");

    [RelayCommand]
    private void CopyLastTxKey() => Copy(LastTxKey, "Transaction key");

    /// <summary>How a recipient is shown: a contact's name, one of your own wallets, or a short address.</summary>
    private string Who(string address) =>
        _profile.ContactNameFor(address)
        ?? (address == PrimaryAddress ? "This wallet" : null)
        ?? (_profile.NameOfWalletWithAddress(address) is { } own ? own + " (your wallet)" : null)
        ?? (address.Length > 20 ? address[..8] + "…" + address[^8..] : address);

    /// <summary>Step 1 of sending: validate the form, BUILD the transaction without broadcasting it
    /// (do_not_relay), and show the exact fee in the confirmation overlay. Monero transactions are
    /// irreversible, so the user confirms with the real cost in front of them; problems like
    /// "not enough money for amount + fee" surface here, before anything is committed.</summary>
    [RelayCommand]
    private async Task ReviewSendAsync()
    {
        // Re-entrancy guard: the form stays keyboard-reachable under the confirm overlay, and a
        // second prepare racing a pending confirm can desync display from broadcast. One prepared
        // tx at a time, driven only by the overlay's buttons.
        if (Sending || ShowSendConfirm)
        {
            return;
        }

        SendResult = string.Empty;

        if (!IsReady)
        {
            SendResult = "Wallet isn't ready yet.";
            return;
        }

        if (!CanSpend)
        {
            SendResult = "This is a watch-only wallet: it can't send.";
            return;
        }

        // Every recipient: a plausible address on THIS network and a valid amount. monero-wallet-rpc
        // remains the final authority; this catches wrong-network and truncated-paste mistakes early.
        var destinations = new List<(string address, decimal xmr)>();
        var rows = new List<(string address, string amountText, string label)>
        {
            (SendAddress, SendAmountText, ExtraRecipients.Count > 0 ? "Recipient 1: " : string.Empty),
        };
        rows.AddRange(ExtraRecipients.Select(r => (r.Address, r.AmountText, r.Title + ": ")));
        foreach ((string address, string amountText, string label) in rows)
        {
            if (MoneroAddress.Problem(address, _secrets.Network) is { } problem)
            {
                SendResult = label + problem;
                return;
            }

            if (!XmrAmount.TryParse(amountText, out decimal amount, out string? amountError))
            {
                SendResult = label + (amountError ?? "Enter a valid amount.");
                return;
            }

            destinations.Add((address.Trim(), amount));
        }

        decimal total = destinations.Sum(d => d.xmr);
        if (total > UnlockedBalance)
        {
            string balance = HideBalances ? string.Empty : $" ({XmrAmount.Format(UnlockedBalance)} XMR)"; // hidden means hidden
            SendResult = $"That's more than your spendable balance{balance}. " +
                         "Some balance may still be maturing, and the network fee comes on top.";
            return;
        }

        Sending = true;
        try
        {
            // Snapshot EVERYTHING the tx is built from BEFORE the await. The overlay displays only
            // these snapshots — nothing typed into the form while the prepare is in flight (the
            // fields stay editable) can make the display disagree with what the tx actually pays.
            uint priority = (uint)Math.Clamp(SendPriority, 0, 3);
            string prio = priority switch { 1 => "Low", 2 => "Medium", 3 => "High", _ => "Default" };

            _replacedAddresses.Clear();
            await ApplyFrozenCoinsAsync(_cts.Token);
            TransferResult prepared = await _wallet.PrepareSendAsync(destinations, AccountIndex, priority, _cts.Token);
            if (destinations.Any(d => _replacedAddresses.Contains(d.address)))
            {
                SendResult = ReplacedWhileBuilding; // built for an address its contact no longer has
                return;
            }

            _preparedTx = prepared;
            _preparedAmount = total;
            ConfirmLines.Clear();
            foreach ((string address, decimal xmr) in destinations)
            {
                ConfirmLines.Add(new ConfirmLine(Who(address), address, $"{XmrAmount.Format(xmr)} XMR"));
            }

            ConfirmHasSeveral = destinations.Count > 1;
            ConfirmSendAddress = destinations.Count == 1 ? destinations[0].address : string.Empty;
            ConfirmRecipientName = destinations.Count == 1 ? _profile.ContactNameFor(destinations[0].address) ?? string.Empty : string.Empty;
            _lastRecipientAddress = destinations.Count == 1 ? destinations[0].address : string.Empty;

            decimal fee = MoneroRpcClient.AtomicToXmr(_preparedTx.Fee);
            string several = destinations.Count > 1 ? $" · {destinations.Count} recipients" : string.Empty;
            SendSummary = $"{prio} priority{several}{FrozenNote()} · built and signed, not yet broadcast";
            ConfirmAmountText = $"{XmrAmount.Format(total)} XMR";
            SendFeeText = $"{XmrAmount.Format(fee)} XMR";
            SendTotalText = $"{XmrAmount.Format(total + fee)} XMR";
            SendFeeWarning = FeeWarning(fee, total);
            ShowSendConfirm = true;
        }
        catch (OperationCanceledException)
        {
            // wallet locked/closed mid-prepare; nothing to report
        }
        catch (Exception ex)
        {
            _preparedTx = null;
            SendResult = "Couldn't prepare the transaction: " + Friendly(ex) + FrozenHint(ex);
        }
        finally
        {
            Sending = false;
        }
    }

    /// <summary>Send Max: build transactions sweeping the ENTIRE spendable balance (do_not_relay)
    /// through the same review→confirm→relay flow as a normal send. Removes the guess-the-fee
    /// dance when emptying a wallet — the overlay shows the exact swept amount and total fee.</summary>
    [RelayCommand]
    private async Task ReviewSendMaxAsync()
    {
        if (Sending || ShowSendConfirm)
        {
            return;
        }

        SendResult = string.Empty;

        if (!IsReady)
        {
            SendResult = "Wallet isn't ready yet.";
            return;
        }

        if (!CanSpend)
        {
            SendResult = "This is a watch-only wallet: it can't send.";
            return;
        }

        if (ExtraRecipients.Count > 0)
        {
            SendResult = "Send max pays a single recipient. Remove the other recipients first.";
            return;
        }

        if (MoneroAddress.Problem(SendAddress, _secrets.Network) is { } problem)
        {
            SendResult = problem;
            return;
        }

        if (UnlockedBalance <= 0m)
        {
            SendResult = "Nothing is spendable right now.";
            return;
        }

        Sending = true;
        try
        {
            uint priority = (uint)Math.Clamp(SendPriority, 0, 3);
            string destination = SendAddress.Trim();

            _replacedAddresses.Clear();
            await ApplyFrozenCoinsAsync(_cts.Token);
            SweepAllResult sweep = await _wallet.PrepareSweepAllAsync(destination, AccountIndex, priority, _cts.Token);
            if (_replacedAddresses.Contains(destination))
            {
                SendResult = ReplacedWhileBuilding;
                return;
            }

            _preparedSweep = sweep;
            ConfirmSendAddress = destination;
            ConfirmRecipientName = _profile.ContactNameFor(destination) ?? string.Empty;
            _lastRecipientAddress = destination;
            ConfirmHasSeveral = false;

            decimal amount = MoneroRpcClient.AtomicToXmr((ulong)sweep.AmountList.Sum(a => (decimal)a));
            decimal fee = MoneroRpcClient.AtomicToXmr((ulong)sweep.FeeList.Sum(f => (decimal)f));
            _preparedAmount = amount;
            ConfirmLines.Clear();
            ConfirmLines.Add(new ConfirmLine(Who(destination), destination, $"{XmrAmount.Format(amount)} XMR"));

            string txNote = sweep.TxMetadataList.Count > 1 ? $" across {sweep.TxMetadataList.Count} transactions" : "";
            SendSummary = $"Sweep of ALL spendable funds{txNote}{FrozenNote()} · not yet broadcast";
            ConfirmAmountText = $"{XmrAmount.Format(amount)} XMR";
            SendFeeText = $"{XmrAmount.Format(fee)} XMR";
            SendTotalText = $"{XmrAmount.Format(amount + fee)} XMR";
            SendFeeWarning = FeeWarning(fee, amount);
            ShowSendConfirm = true;
        }
        catch (OperationCanceledException)
        {
            // wallet locked/closed mid-prepare; nothing to report
        }
        catch (Exception ex)
        {
            _preparedSweep = null;
            SendResult = "Couldn't prepare the sweep: " + Friendly(ex) + FrozenHint(ex);
        }
        finally
        {
            Sending = false;
        }
    }

    /// <summary>A fee wildly out of proportion to the amount usually means a misbehaving node's
    /// fee estimate (or a unit mishap) — say so instead of letting habit click through it.</summary>
    private static string FeeWarning(decimal fee, decimal amount) =>
        amount > 0m && fee > 0.001m && fee > amount * 0.01m
            ? $"This fee is unusually high ({(fee / amount).ToString("P1", CultureInfo.InvariantCulture)} of the amount). If you didn't choose a high priority on purpose, cancel and check your node."
            : string.Empty;

    /// <summary>Abort: throw away the prepared (never-broadcast) transaction(s).</summary>
    [RelayCommand]
    private void CancelSend()
    {
        ShowSendConfirm = false;
        _preparedTx = null;
        _preparedSweep = null;
        ConfirmSendAddress = string.Empty;
        ConfirmRecipientName = string.Empty;
        ConfirmLines.Clear();
        SendFeeWarning = string.Empty;
        _preparedAmount = 0m;
    }

    /// <summary>Step 2: the user explicitly confirmed. Broadcast the ALREADY-BUILT transaction(s) —
    /// the fee shown in the overlay is baked into them and cannot change.</summary>
    [RelayCommand]
    private async Task ConfirmSendAsync()
    {
        ShowSendConfirm = false;
        SendFeeWarning = string.Empty;
        TransferResult? prepared = _preparedTx;
        SweepAllResult? sweep = _preparedSweep;
        _preparedTx = null;
        _preparedSweep = null;

        if (sweep is not null)
        {
            await ConfirmSweepAsync(sweep);
            return;
        }

        if (prepared is null)
        {
            // Never send blind — but an explicitly-clicked confirm must never LOOK like a send.
            SendResult = "Nothing was broadcast — the prepared transaction was no longer available. " +
                         "Review the send again.";
            return;
        }

        Sending = true;
        try
        {
            string txHash = await _wallet.RelaySendAsync(prepared.TxMetadata, _cts.Token);
            decimal fee = MoneroRpcClient.AtomicToXmr(prepared.Fee);
            string several = ConfirmLines.Count > 1 ? $" to {ConfirmLines.Count} recipients" : string.Empty;
            SendResult = $"Sent {XmrAmount.Format(_preparedAmount)} XMR{several}";
            SendResultDetail = $"Network fee {XmrAmount.Format(fee)} XMR. It confirms in about 2 minutes; " +
                               "your change is spendable again after 10 confirmations (about 20 minutes).";
            SendOutcome = SendOutcome.Sent;

            // Surface the transaction key so the payment can be proven on an explorer.
            LastTxId = string.IsNullOrWhiteSpace(txHash) ? prepared.TxHash : txHash;
            LastTxKey = prepared.TxKey;
            HasLastTx = !string.IsNullOrWhiteSpace(LastTxId);
            OfferSaveRecipient = _lastRecipientAddress.Length > 0 && _profile.ContactNameFor(_lastRecipientAddress) is null;

            ClearSendForm();
            await SoftRefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // The broadcast request may already have reached the network before cancellation.
            SendResult = "Send interrupted — the transaction MAY still have been broadcast. " +
                         $"Check History for txid {prepared.TxHash} before sending again.";
            SendOutcome = SendOutcome.NeedsCheck;
        }
        catch (Exception ex)
        {
            // Honesty over reassurance: a failed-looking relay can still have reached the network,
            // and retrying too early can double-pay with a second transaction. Give the txid so
            // "check History" is actually actionable.
            SendResult = "Broadcast failed: " + Friendly(ex) +
                         $" The transaction may or may not have reached the network — check History for txid {prepared.TxHash} before sending again.";
            SendOutcome = SendOutcome.NeedsCheck;
        }
        finally
        {
            ConfirmSendAddress = string.Empty;
            ConfirmRecipientName = string.Empty;
            ConfirmLines.Clear();
            _preparedAmount = 0m;
            Sending = false;
        }
    }

    private void ClearSendForm()
    {
        SendAddress = string.Empty;
        SendAmountText = string.Empty;
        _payee = null;
        SelectedSendContact = null;
        PaymentRequestNote = string.Empty;
        ExtraRecipients.Clear();
        OnPropertyChanged(nameof(HasExtraRecipients));
    }

    /// <summary>Broadcast every transaction of a confirmed sweep, in order. On a mid-sweep
    /// failure, reports EXACTLY which transactions went out — never pretends an ambiguous
    /// state is a clean failure.</summary>
    private async Task ConfirmSweepAsync(SweepAllResult sweep)
    {
        Sending = true;
        int relayed = 0;
        try
        {
            for (int i = 0; i < sweep.TxMetadataList.Count; i++)
            {
                string txHash = await _wallet.RelaySendAsync(sweep.TxMetadataList[i], _cts.Token);
                if (string.IsNullOrWhiteSpace(txHash) && i < sweep.TxHashList.Count)
                {
                    txHash = sweep.TxHashList[i];
                }

                relayed++;
                LastTxId = txHash;
                LastTxKey = i < sweep.TxKeyList.Count ? sweep.TxKeyList[i] : string.Empty;
            }

            HasLastTx = !string.IsNullOrWhiteSpace(LastTxId);
            decimal fee = MoneroRpcClient.AtomicToXmr((ulong)sweep.FeeList.Sum(f => (decimal)f));
            string txNote = relayed > 1 ? $" in {relayed} transactions" : "";
            SendResult = $"Swept {XmrAmount.Format(_preparedAmount)} XMR{txNote}";
            SendResultDetail = $"Total network fee {XmrAmount.Format(fee)} XMR. It confirms in about 2 minutes.";
            SendOutcome = SendOutcome.Sent;
            OfferSaveRecipient = _lastRecipientAddress.Length > 0 && _profile.ContactNameFor(_lastRecipientAddress) is null;
            ClearSendForm();
            await SoftRefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || relayed > 0)
        {
            string done = relayed == 0
                ? "No transaction is confirmed sent, but the first MAY still have reached the network."
                : $"{relayed} of {sweep.TxMetadataList.Count} transactions were broadcast before the failure.";
            SendResult = $"Sweep interrupted: {Friendly(ex)} {done} Check History before retrying — " +
                         "re-running the sweep too early can conflict with the transactions already sent.";
            SendOutcome = SendOutcome.NeedsCheck;
        }
        catch (OperationCanceledException)
        {
            // Same honesty as the single-send path: the in-flight relay (relayed == 0 here —
            // a cancel after a successful relay is handled above) may already have reached the
            // network. Point at the first prepared txid when we have one.
            string tip = sweep.TxHashList.Count > 0 ? $"txid {sweep.TxHashList[0]}" : "the sweep";
            SendResult = "Sweep interrupted — a transaction MAY still have been broadcast. " +
                         $"Check History for {tip} before sweeping again.";
            SendOutcome = SendOutcome.NeedsCheck;
        }
        finally
        {
            ConfirmSendAddress = string.Empty;
            ConfirmRecipientName = string.Empty;
            ConfirmLines.Clear();
            _preparedAmount = 0m;
            Sending = false;
        }
    }

    /// <summary>"Pay" from the Contacts tab: fill the form with that contact — its address as it is
    /// now, even when the form already had the contact picked.</summary>
    internal void PayContact(ContactRow contact)
    {
        SendResult = string.Empty;
        _payee = contact;
        SendAddress = contact.Address;
        SelectedSendContact = contact;
        SelectedTab = 1;
    }
}
