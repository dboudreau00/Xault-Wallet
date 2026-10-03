using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

public sealed partial class WalletViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly WalletSecrets _secrets;
    private MoneroWalletService _wallet;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Task? _autoRefreshLoop;
    private bool _disposed;

    [ObservableProperty] private string _status = "Starting wallet\u2026";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _startupFailed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockedBalance))]
    [NotifyPropertyChangedFor(nameof(HasLocked))]
    [NotifyPropertyChangedFor(nameof(BalanceDisplay))]
    [NotifyPropertyChangedFor(nameof(UnlockedDisplay))]
    [NotifyPropertyChangedFor(nameof(LockedDisplay))]
    private decimal _balance;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockedBalance))]
    [NotifyPropertyChangedFor(nameof(HasLocked))]
    [NotifyPropertyChangedFor(nameof(BalanceDisplay))]
    [NotifyPropertyChangedFor(nameof(UnlockedDisplay))]
    [NotifyPropertyChangedFor(nameof(LockedDisplay))]
    private decimal _unlockedBalance;

    /// <summary>Balance still maturing (total minus spendable). Never negative.</summary>
    public decimal LockedBalance => Math.Max(0m, Balance - UnlockedBalance);
    public bool HasLocked => LockedBalance > 0m;
    [ObservableProperty] private ulong _height;
    [ObservableProperty] private string _primaryAddress = string.Empty;

    // Node sync tracker
    [ObservableProperty] private double _syncProgress;          // 0..100
    [ObservableProperty] private ulong _daemonHeight;
    [ObservableProperty] private bool _isSynced;
    [ObservableProperty] private string _syncText = "Connecting to node\u2026";

    // Send tab
    [ObservableProperty] private string _sendAddress = string.Empty;

    /// <summary>The amount exactly as typed. Parsed by <see cref="XmrAmount"/> (culture-independent)
    /// — never bound as a number, because culture-aware conversion turned "0,25" into 25 XMR.</summary>
    [ObservableProperty] private string _sendAmountText = string.Empty;

    /// <summary>Live read-back of how the typed amount is understood, e.g. "= 0.25 XMR".</summary>
    [ObservableProperty] private string _sendAmountPreview = string.Empty;
    [ObservableProperty] private bool _sendAmountPreviewIsError;

    partial void OnSendAmountTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SendAmountPreview = string.Empty;
            SendAmountPreviewIsError = false;
            return;
        }

        if (XmrAmount.TryParse(value, out decimal xmr, out string? error))
        {
            SendAmountPreview = XmrAmount.LooksThousandsGrouped(value)
                ? $"= {XmrAmount.Format(xmr)} XMR — the separator is read as a decimal point"
                : $"= {XmrAmount.Format(xmr)} XMR";
            SendAmountPreviewIsError = false;
        }
        else
        {
            SendAmountPreview = error ?? "Invalid amount.";
            SendAmountPreviewIsError = true;
        }
    }
    [ObservableProperty] private int _sendPriority = 1;
    [ObservableProperty] private string _sendResult = string.Empty;
    [ObservableProperty] private bool _sending;

    // Send confirmation overlay (irreversible action — always confirm)
    [ObservableProperty] private bool _showSendConfirm;
    [ObservableProperty] private string _sendSummary = string.Empty;
    [ObservableProperty] private string _sendFeeText = string.Empty;
    [ObservableProperty] private string _sendTotalText = string.Empty;

    /// <summary>Snapshot of the destination the prepared tx actually pays. The overlay binds to
    /// THIS, not the live SendAddress field — so nothing typed under the overlay can make the
    /// display disagree with what Confirm broadcasts.</summary>
    [ObservableProperty] private string _confirmSendAddress = string.Empty;

    // Transaction built by ReviewSend (do_not_relay) and broadcast only on explicit confirm.
    // Holds the exact fee; discarding it (Cancel) means nothing ever touches the network.
    private TransferResult? _preparedTx;
    private decimal _preparedAmount;

    // Prepared sweep-all (Send max): same do_not_relay contract, but may span several
    // transactions. Mutually exclusive with _preparedTx — exactly one is non-null while the
    // confirm overlay is up.
    private SweepAllResult? _preparedSweep;

    /// <summary>Set when the prepared fee is anomalously high relative to the amount —
    /// a habituated user shouldn't be able to click through a fee spike unwarned.</summary>
    [ObservableProperty] private string _sendFeeWarning = string.Empty;

    // Auto-lock countdown: visible warning strip shortly before the inactivity lock fires.
    [ObservableProperty] private bool _lockImminent;
    [ObservableProperty] private string _lockCountdownText = string.Empty;

    /// <summary>Masks balances on screen (shoulder-surfing). Persisted in settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceDisplay))]
    [NotifyPropertyChangedFor(nameof(UnlockedDisplay))]
    [NotifyPropertyChangedFor(nameof(LockedDisplay))]
    private bool _hideBalances = AppServices.Instance.Settings.HideBalances;

    private const string Masked = "●●●●●";
    public string BalanceDisplay => HideBalances ? Masked : XmrAmount.Format(Balance);
    public string UnlockedDisplay => HideBalances ? $"Spendable now: {Masked}" : $"Spendable now: {XmrAmount.Format(UnlockedBalance)} XMR";
    public string LockedDisplay => HideBalances ? $"⧗ {Masked}" : $"⧗ {XmrAmount.Format(LockedBalance)} XMR maturing";

    [RelayCommand]
    private void ToggleBalances()
    {
        HideBalances = !HideBalances;
        try
        {
            AppServices.Instance.Settings.HideBalances = HideBalances;
            AppServices.Instance.SaveSettings();
        }
        catch
        {
            // Persisting the preference is best-effort; the toggle itself already applied.
        }
    }

    /// <summary>Which Monero network this wallet is on — shown as a badge so a real-funds
    /// mainnet wallet is never mistaken for a test one (or vice versa).</summary>
    public string NetworkLabel => _secrets.Network.ToString();
    public bool IsMainnetWallet => _secrets.Network == Core.Models.MoneroNetwork.Mainnet;

    /// <summary>Feedback line for the Receive tab (new-subaddress errors, copy feedback) —
    /// kept OFF the Status line, which the sync tracker overwrites every few seconds.</summary>
    [ObservableProperty] private string _receiveNotice = string.Empty;

    /// <summary>Transient "Copied — clipboard clears in 30 s" feedback, set by the view.</summary>
    [ObservableProperty] private string _copyNotice = string.Empty;

    // Payment proof (the tx key from the most recent send — safe to share for explorer verification)
    [ObservableProperty] private bool _hasLastTx;
    [ObservableProperty] private string _lastTxId = string.Empty;
    [ObservableProperty] private string _lastTxKey = string.Empty;

    // Verify-a-payment panel
    [ObservableProperty] private string _verifyTxId = string.Empty;
    [ObservableProperty] private string _verifyTxKey = string.Empty;
    [ObservableProperty] private string _verifyAddress = string.Empty;
    [ObservableProperty] private string _verifyResult = string.Empty;
    [ObservableProperty] private bool _verifyOk;

    public ObservableCollection<TransferEntry> History { get; } = new();

    /// <summary>Drives the History tab's empty-state hint.</summary>
    [ObservableProperty] private bool _hasHistory;

    public event Action? Locked;

    /// <summary>Raised when the user asks to open Settings from the wallet screen (e.g. the
    /// startup-failure banner). The shell (MainWindowViewModel) handles the actual navigation.</summary>
    public event Action? SettingsRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    public WalletViewModel(WalletSecrets secrets)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _wallet = AppServices.Instance.CreateWalletService();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        StartupFailed = false;
        IsReady = false;
        try
        {
            Status = "Restoring wallet from seed\u2026";
            await _wallet.OpenAsync(_secrets, _cts.Token);
            PrimaryAddress = await _wallet.GetPrimaryAddressAsync(_cts.Token);
            IsReady = true;
            Status = "Syncing in the background\u2026";

            await SoftRefreshAsync();
            // Start the polling loop WITHOUT Task.Run so its awaits resume on the UI thread —
            // it mutates bound collections/properties, which must happen on the UI thread.
            _autoRefreshLoop ??= AutoRefreshLoopAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Locked/closed during startup; nothing to do.
        }
        catch (Exception ex)
        {
            Log.Error("Wallet startup failed", ex);
            StartupFailed = true;
            Status = "Couldn't start the wallet. " + Friendly(ex);
        }
    }

    [RelayCommand]
    private async Task RetryStartupAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        // Rebuild the wallet service so a monero-wallet-rpc path (or default node) just changed in
        // Settings actually takes effect — the service captures the binary path at construction.
        try { await _wallet.DisposeAsync(); } catch { /* best effort */ }
        _wallet = AppServices.Instance.CreateWalletService();

        await InitializeAsync();
    }

    private DateTime _lastActivityUtc = DateTime.UtcNow;

    /// <summary>Called from the window on any user input to defer auto-lock.</summary>
    public void NotifyActivity() => _lastActivityUtc = DateTime.UtcNow;

    private async Task AutoRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            DateTime nextRefreshUtc = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= nextRefreshUtc)
                {
                    await SoftRefreshAsync();

                    // Snappy updates while the node is catching up; relaxed once synced.
                    int delayMs = IsSynced
                        ? Math.Clamp(AppServices.Instance.AutoRefreshSeconds, 5, 600) * 1000
                        : 3000;
                    nextRefreshUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
                }

                // Auto-lock after inactivity (0 = disabled), with a visible countdown for the
                // last 30 seconds so the wallet never just vanishes mid-read.
                int lockMinutes = AppServices.Instance.AutoLockMinutes;
                if (lockMinutes > 0)
                {
                    TimeSpan remaining = TimeSpan.FromMinutes(lockMinutes) - (DateTime.UtcNow - _lastActivityUtc);
                    if (remaining <= TimeSpan.Zero)
                    {
                        Log.Info("Auto-locking after inactivity.");
                        LockImminent = false;
                        // Fire-and-forget, then RETURN so this loop task can complete.
                        // Awaiting LockAsync here would deadlock: it disposes the VM, which
                        // awaits this very task — a task can never await itself finishing.
                        _ = LockAsync();
                        return;
                    }

                    LockImminent = remaining <= TimeSpan.FromSeconds(30);
                    if (LockImminent)
                    {
                        LockCountdownText = $"Locking in {Math.Max(1, (int)remaining.TotalSeconds)} s due to inactivity";
                    }
                }
                else
                {
                    LockImminent = false;
                }

                // Short tick so the countdown stays live; the RPC refresh above still runs on
                // its own (much slower) cadence.
                await Task.Delay(LockImminent ? 1000 : 3000, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on lock/dispose
        }
        catch (Exception ex)
        {
            Log.Warn("Auto-refresh loop ended: " + ex.GetType().Name);
        }
    }

    /// <summary>The "Stay unlocked" button on the countdown strip — any activity defers the lock.</summary>
    [RelayCommand]
    private void StayUnlocked()
    {
        NotifyActivity();
        LockImminent = false;
    }

    /// <summary>Non-blocking refresh: reads current balance/height/history. Errors are soft. </summary>
    private async Task SoftRefreshAsync()
    {
        if (!IsReady || _disposed)
        {
            return;
        }

        // Skip (don't queue) if a refresh is already in flight. The gate can be disposed by a
        // concurrent lock/close racing this call.
        try
        {
            if (!await _refreshGate.WaitAsync(0))
            {
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            (Balance, UnlockedBalance) = await _wallet.GetBalanceAsync(_cts.Token);
            Height = await _wallet.GetHeightAsync(_cts.Token);

            // Node sync tracker: compare the wallet's scanned height against the daemon's tip.
            try
            {
                DaemonHeight = await MoneroDiagnostics.ProbeDaemonAsync(_secrets.DaemonAddress, AppServices.Instance.Settings.ProxyAddress, _cts.Token);
            }
            catch
            {
                DaemonHeight = 0; // daemon momentarily unreachable; keep last balances
            }
            UpdateSyncStatus();

            // History often isn't available until the wallet finishes scanning; a transient
            // get_transfers failure here should NOT flip the sync status. Keep last known list.
            try
            {
                IReadOnlyList<TransferEntry> entries = await _wallet.GetHistoryAsync(_cts.Token);

                // Rebuild only when something changed: clearing every refresh reset the user's row
                // selection (mid "copy tx ID") and scroll position every 20 seconds.
                if (!SameHistory(History, entries))
                {
                    History.Clear();
                    foreach (TransferEntry t in entries)
                    {
                        History.Add(t);
                    }
                }

                HasHistory = History.Count > 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_historyNotReadyLogged)
                {
                    _historyNotReadyLogged = true; // once per session, not every few seconds
                    Log.Info("Transaction history not ready yet: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Distinguish "backend process died" from "node is slow": repeated connection
            // errors against a dead wallet-rpc would otherwise read as a sync hiccup forever.
            // The startup-failure banner offers Retry, which rebuilds the whole service.
            if (_wallet.BackendExited)
            {
                Log.Error("Wallet backend process exited unexpectedly.");
                StartupFailed = true;
                IsReady = false;
                Status = "The wallet backend stopped unexpectedly. Use Retry to restart it.";
            }
            else
            {
                SyncText = "Sync issue: " + Friendly(ex);
            }
        }
        finally
        {
            // The gate can be disposed by a concurrent lock/close racing a manual refresh.
            try { _refreshGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    private bool _historyNotReadyLogged;

    private static bool SameHistory(IList<TransferEntry> shown, IReadOnlyList<TransferEntry> fresh)
    {
        if (shown.Count != fresh.Count)
        {
            return false;
        }

        for (int i = 0; i < fresh.Count; i++)
        {
            TransferEntry a = shown[i], b = fresh[i];
            if (a.TxId != b.TxId || a.Type != b.Type || a.Height != b.Height || a.Amount != b.Amount || a.Timestamp != b.Timestamp)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateSyncStatus()
    {
        ulong wallet = Height;
        ulong node = DaemonHeight;

        if (node == 0)
        {
            IsSynced = false;
            SyncProgress = 0;
            SyncText = "Connecting to node\u2026";
            Status = SyncText;
            return;
        }

        if (wallet + 1 >= node)
        {
            IsSynced = true;
            SyncProgress = 100;
            SyncText = $"Synced \u00b7 block {node.ToString("N0", Inv)}";
        }
        else
        {
            IsSynced = false;
            // Progress over the part of the chain this wallet actually scans (restore height → tip),
            // not from genesis: a wallet restored near the tip used to show "97%" while scanning.
            ulong start = Math.Min(_secrets.RestoreHeight, node);
            double done = wallet > start ? wallet - start : 0;
            SyncProgress = Math.Clamp(100.0 * done / Math.Max(1, node - start), 0, 99.9);
            ulong behind = node - wallet;
            SyncText = $"Syncing \u00b7 {SyncProgress.ToString("0.0", Inv)}%  \u00b7  {wallet.ToString("N0", Inv)} / {node.ToString("N0", Inv)}  ({behind.ToString("N0", Inv)} behind)";
        }

        Status = SyncText;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsReady)
        {
            return;
        }

        // Force a synchronous refresh, but never let a slow/hung refresh wedge the UI.
        try
        {
            Status = "Refreshing\u2026";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await _wallet.RefreshAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            Status = "Refresh is taking a while; still syncing in the background.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Status = "Refresh failed: " + Friendly(ex);
        }

        await SoftRefreshAsync();
    }

    /// <summary>Step 1 of sending: validate the form, BUILD the transaction without broadcasting
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

        // Sanity-check the address (charset/length/network prefix). monero-wallet-rpc remains
        // the final authority; this catches wrong-network and truncated-paste mistakes early.
        if (MoneroAddress.Problem(SendAddress, _secrets.Network) is { } problem)
        {
            SendResult = problem;
            return;
        }

        if (!XmrAmount.TryParse(SendAmountText, out decimal amount, out string? amountError))
        {
            SendResult = amountError ?? "Enter a valid amount.";
            return;
        }

        if (amount > UnlockedBalance)
        {
            SendResult = $"Amount exceeds your spendable balance ({XmrAmount.Format(UnlockedBalance)} XMR). " +
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
            string destination = SendAddress.Trim();
            string prio = priority switch { 1 => "Low", 2 => "Medium", 3 => "High", _ => "Default" };

            _preparedTx = await _wallet.PrepareSendAsync(destination, amount, priority, _cts.Token);
            ConfirmSendAddress = destination;
            _preparedAmount = amount;

            decimal fee = MoneroRpcClient.AtomicToXmr(_preparedTx.Fee);
            SendSummary = $"Send {XmrAmount.Format(amount)} XMR ({prio} priority) to:";
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
            _preparedTx = null;
            SendResult = "Couldn't prepare the transaction: " + Friendly(ex);
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

            SweepAllResult sweep = await _wallet.PrepareSweepAllAsync(destination, priority, _cts.Token);
            _preparedSweep = sweep;
            ConfirmSendAddress = destination;

            decimal amount = MoneroRpcClient.AtomicToXmr((ulong)sweep.AmountList.Sum(a => (decimal)a));
            decimal fee = MoneroRpcClient.AtomicToXmr((ulong)sweep.FeeList.Sum(f => (decimal)f));
            _preparedAmount = amount;

            string txNote = sweep.TxMetadataList.Count > 1 ? $" across {sweep.TxMetadataList.Count} transactions" : "";
            SendSummary = $"Sweep ALL spendable funds ({XmrAmount.Format(amount)} XMR{txNote}) to:";
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
            SendResult = "Couldn't prepare the sweep: " + Friendly(ex);
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
            ? $"This fee is unusually high ({(fee / amount).ToString("P1", System.Globalization.CultureInfo.InvariantCulture)} of the amount). If you didn't choose a high priority on purpose, cancel and check your node."
            : string.Empty;

    /// <summary>Abort: throw away the prepared (never-broadcast) transaction(s).</summary>
    [RelayCommand]
    private void CancelSend()
    {
        ShowSendConfirm = false;
        _preparedTx = null;
        _preparedSweep = null;
        ConfirmSendAddress = string.Empty;
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
            SendResult = $"Sent {XmrAmount.Format(_preparedAmount)} XMR (fee {XmrAmount.Format(fee)} XMR).";

            // Surface the transaction key so the payment can be proven on an explorer.
            LastTxId = string.IsNullOrWhiteSpace(txHash) ? prepared.TxHash : txHash;
            LastTxKey = prepared.TxKey;
            HasLastTx = !string.IsNullOrWhiteSpace(LastTxId);

            SendAddress = string.Empty;
            SendAmountText = string.Empty;
            await SoftRefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // The broadcast request may already have reached the network before cancellation.
            SendResult = "Send interrupted — the transaction MAY still have been broadcast. " +
                         $"Check History for txid {prepared.TxHash} before sending again.";
        }
        catch (Exception ex)
        {
            // Honesty over reassurance: a failed-looking relay can still have reached the network,
            // and retrying too early can double-pay with a second transaction. Give the txid so
            // "check History" is actually actionable.
            SendResult = "Broadcast failed: " + Friendly(ex) +
                         $" The transaction may or may not have reached the network — check History for txid {prepared.TxHash} before sending again.";
        }
        finally
        {
            ConfirmSendAddress = string.Empty;
            _preparedAmount = 0m;
            Sending = false;
        }
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
            SendResult = $"Swept {XmrAmount.Format(_preparedAmount)} XMR{txNote} (total fee {XmrAmount.Format(fee)} XMR).";
            SendAddress = string.Empty;
            SendAmountText = string.Empty;
            await SoftRefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || relayed > 0)
        {
            string done = relayed == 0
                ? "No transaction is confirmed sent, but the first MAY still have reached the network."
                : $"{relayed} of {sweep.TxMetadataList.Count} transactions were broadcast before the failure.";
            SendResult = $"Sweep interrupted: {Friendly(ex)} {done} Check History before retrying — " +
                         "re-running the sweep too early can conflict with the transactions already sent.";
        }
        catch (OperationCanceledException)
        {
            // wallet locked/closed before anything went out
        }
        finally
        {
            ConfirmSendAddress = string.Empty;
            _preparedAmount = 0m;
            Sending = false;
        }
    }

    /// <summary>History as CSV (spreadsheet-friendly). The view handles the file picker.</summary>
    public string BuildHistoryCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("date,type,amount_xmr,fee_xmr,height,txid");
        foreach (TransferEntry t in History)
        {
            // txid/type/date contain no commas or quotes (hex, fixed words, fixed format).
            sb.Append(t.Date).Append(',')
              .Append(t.Type).Append(',')
              .Append(MoneroRpcClient.AtomicToXmr(t.Amount).ToString("0.############", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(MoneroRpcClient.AtomicToXmr(t.Fee).ToString("0.############", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(t.Height).Append(',')
              .Append(t.TxId).AppendLine();
        }

        return sb.ToString();
    }

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
    private async Task NewAddressAsync()
    {
        if (!IsReady)
        {
            return;
        }

        try
        {
            ReceiveNotice = string.Empty;
            PrimaryAddress = await _wallet.NewSubaddressAsync("", _cts.Token);
        }
        catch (Exception ex)
        {
            // NOT Status: the sync tracker overwrites Status every few seconds, so the
            // failure would vanish before the user saw it.
            ReceiveNotice = "Couldn't create a new address: " + Friendly(ex);
        }
    }

    [RelayCommand]
    private async Task LockAsync()
    {
        await DisposeAsync();
        Locked?.Invoke();
    }

    private static string Friendly(Exception ex) => ex switch
    {
        FileNotFoundException => "monero-wallet-rpc was not found. Set its path in Settings.",
        TimeoutException => "the wallet backend didn't respond in time.",
        MoneroRpcClient.MoneroRpcException rpc => FriendlyRpc(rpc),
        _ => ex.Message,
    };

    /// <summary>Map raw monero-wallet-rpc errors to human messages (matched on message text —
    /// the numeric codes are less stable across releases). Unknown errors pass through, already
    /// secret-redacted at the source.</summary>
    private static string FriendlyRpc(MoneroRpcClient.MoneroRpcException rpc)
    {
        string m = rpc.Message;

        if (Has(m, "not enough unlocked money") || Has(m, "not enough money"))
        {
            return "Not enough spendable balance to cover the amount plus the network fee. " +
                   "Wait for incoming funds to unlock, or lower the amount.";
        }

        if (Has(m, "wrong address") || Has(m, "invalid address") || Has(m, "invalid destination"))
        {
            return "The destination address was rejected by the wallet backend. Re-check it.";
        }

        if (Has(m, "daemon is busy"))
        {
            return "The node is busy (likely still syncing). Try again in a moment.";
        }

        if (Has(m, "no connection to daemon") || Has(m, "connection to daemon"))
        {
            return "Lost connection to the node. Check the node in Settings, or try again shortly.";
        }

        if (Has(m, "double spend") || Has(m, "tx not possible") || Has(m, "transaction was rejected"))
        {
            return "The prepared transaction is no longer valid (the wallet state changed). " +
                   "Review the send again to rebuild it.";
        }

        if (Has(m, "fee is too low") || Has(m, "fee too low"))
        {
            return "The network rejected the fee as too low. Try a higher priority.";
        }

        return m;
    }

    private static bool Has(string message, string fragment) =>
        message.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try { await _cts.CancelAsync(); } catch { }

        if (_autoRefreshLoop is not null)
        {
            try { await _autoRefreshLoop; } catch { }
        }

        try
        {
            await _wallet.DisposeAsync();
        }
        catch (Exception ex)
        {
            // A wedged monero-wallet-rpc during teardown must not make Lock appear to do
            // nothing (or fault app shutdown) — the process kill is already best-effort.
            Log.Warn("Wallet service dispose failed: " + ex.GetType().Name);
        }

        _refreshGate.Dispose();
        _cts.Dispose();
    }
}
