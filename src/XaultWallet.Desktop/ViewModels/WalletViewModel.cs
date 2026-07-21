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
    private readonly MoneroWalletService _wallet;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Task? _autoRefreshLoop;
    private bool _disposed;

    [ObservableProperty] private string _status = "Starting wallet\u2026";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _startupFailed;
    [ObservableProperty] private decimal _balance;
    [ObservableProperty] private decimal _unlockedBalance;
    [ObservableProperty] private ulong _height;
    [ObservableProperty] private string _primaryAddress = string.Empty;

    // Node sync tracker
    [ObservableProperty] private double _syncProgress;          // 0..100
    [ObservableProperty] private ulong _daemonHeight;
    [ObservableProperty] private bool _isSynced;
    [ObservableProperty] private string _syncText = "Connecting to node\u2026";

    // Send tab
    [ObservableProperty] private string _sendAddress = string.Empty;
    [ObservableProperty] private decimal _sendAmount;
    [ObservableProperty] private int _sendPriority = 1;
    [ObservableProperty] private string _sendResult = string.Empty;
    [ObservableProperty] private bool _sending;

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

    public string WalletLabel => _secrets.Label;

    public event Action? Locked;

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

        await InitializeAsync();
    }

    private async Task AutoRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await SoftRefreshAsync();

                // Snappy updates while the node is catching up; relaxed once synced.
                int delayMs = IsSynced
                    ? Math.Clamp(AppServices.Instance.AutoRefreshSeconds, 5, 600) * 1000
                    : 3000;
                await Task.Delay(delayMs, ct);
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

    /// <summary>Non-blocking refresh: reads current balance/height/history. Errors are soft. </summary>
    private async Task SoftRefreshAsync()
    {
        if (!IsReady || _disposed)
        {
            return;
        }

        // Skip (don't queue) if a refresh is already in flight.
        if (!await _refreshGate.WaitAsync(0))
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
                DaemonHeight = await MoneroDiagnostics.ProbeDaemonAsync(_secrets.DaemonAddress, _cts.Token);
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
                History.Clear();
                foreach (TransferEntry t in entries)
                {
                    History.Add(t);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Info("Transaction history not ready yet: " + ex.Message);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SyncText = "Sync issue: " + Friendly(ex);
        }
        finally
        {
            _refreshGate.Release();
        }
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
            SyncText = $"Synced \u00b7 block {node:N0}";
        }
        else
        {
            IsSynced = false;
            SyncProgress = Math.Clamp(100.0 * wallet / node, 0, 99.9);
            ulong behind = node - wallet;
            SyncText = $"Syncing \u00b7 {SyncProgress:0.0}%  \u00b7  {wallet:N0} / {node:N0}  ({behind:N0} behind)";
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

    [RelayCommand]
    private async Task SendAsync()
    {
        SendResult = string.Empty;

        if (!IsReady)
        {
            SendResult = "Wallet isn't ready yet.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SendAddress))
        {
            SendResult = "Enter a destination address.";
            return;
        }

        if (SendAmount <= 0m)
        {
            SendResult = "Enter an amount greater than zero.";
            return;
        }

        if (SendAmount > UnlockedBalance)
        {
            SendResult = $"Amount exceeds your unlocked balance ({UnlockedBalance} XMR). " +
                         "Note some balance may still be locked or needed for the fee.";
            return;
        }

        Sending = true;
        try
        {
            uint priority = (uint)Math.Clamp(SendPriority, 0, 3);
            TransferResult r = await _wallet.SendAsync(SendAddress.Trim(), SendAmount, priority, _cts.Token);
            SendResult = $"Sent {SendAmount} XMR (fee {MoneroRpcClient.AtomicToXmr(r.Fee)} XMR).";

            // Surface the transaction key so the payment can be proven on an explorer.
            LastTxId = r.TxHash;
            LastTxKey = r.TxKey;
            HasLastTx = !string.IsNullOrWhiteSpace(r.TxHash);

            Log.Info("Transfer submitted.");
            SendAddress = string.Empty;
            SendAmount = 0;
            await SoftRefreshAsync();
        }
        catch (OperationCanceledException)
        {
            SendResult = "Send cancelled.";
        }
        catch (Exception ex)
        {
            SendResult = "Failed: " + Friendly(ex);
        }
        finally
        {
            Sending = false;
        }
    }

    /// <summary>Copy the last send's txid + tx key into the verify panel as a convenience.</summary>
    [RelayCommand]
    private void PrefillVerifyFromLast()
    {
        VerifyTxId = LastTxId;
        VerifyTxKey = LastTxKey;
        VerifyResult = string.Empty;
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
                VerifyResult = $"Verified: that address received {MoneroRpcClient.AtomicToXmr(received)} XMR — {status}.";
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
            PrimaryAddress = await _wallet.NewSubaddressAsync("", _cts.Token);
        }
        catch (Exception ex)
        {
            Status = "Couldn't create a new address: " + Friendly(ex);
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
        MoneroRpcClient.MoneroRpcException rpc => rpc.Message,
        _ => ex.Message,
    };

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

        await _wallet.DisposeAsync();
        _refreshGate.Dispose();
        _cts.Dispose();
    }
}
