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

    // Send tab
    [ObservableProperty] private string _sendAddress = string.Empty;
    [ObservableProperty] private decimal _sendAmount;
    [ObservableProperty] private int _sendPriority = 1;
    [ObservableProperty] private string _sendResult = string.Empty;
    [ObservableProperty] private bool _sending;

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
            int seconds = Math.Clamp(AppServices.Instance.AutoRefreshSeconds, 5, 600);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await SoftRefreshAsync();
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

            IReadOnlyList<TransferEntry> entries = await _wallet.GetHistoryAsync(_cts.Token);
            History.Clear();
            foreach (TransferEntry t in entries)
            {
                History.Add(t);
            }

            Status = $"Synced to height {Height}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "Sync issue: " + Friendly(ex);
        }
        finally
        {
            _refreshGate.Release();
        }
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
            SendResult = $"Sent. tx: {r.TxHash} (fee {MoneroRpcClient.AtomicToXmr(r.Fee)} XMR)";
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
