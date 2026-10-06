using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>An account of the open wallet, for the account picker.</summary>
public sealed partial class AccountChoice : ObservableObject
{
    public AccountChoice(uint index, string label)
    {
        Index = index;
        _label = label;
    }

    public uint Index { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    [NotifyPropertyChangedFor(nameof(Display))]
    private string _label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private string _balanceText = string.Empty;

    public string Name => Label.Length > 0 ? Label : Index == 0 ? "Primary account" : $"Account #{Index}";

    public string Display => BalanceText.Length > 0 ? $"{Name} · {BalanceText}" : Name;

    public override string ToString() => Name;
}

/// <summary>
/// One open wallet: its own monero-wallet-rpc backend, balance, sync state and the five tabs
/// (receive, send, history, contacts, tools), plus its management sheet. The parts live in
/// WalletViewModel.*.cs. It belongs to a <see cref="ProfileViewModel"/>, which owns the vault
/// session (saving), the contacts and the inactivity lock.
/// </summary>
public sealed partial class WalletViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ProfileViewModel _profile;
    private readonly WalletSecrets _secrets;
    private MoneroWalletService _wallet;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Task? _autoRefreshLoop;
    private bool _disposed;
    private bool _foreground;
    private DateTime _nextRefreshUtc = DateTime.UtcNow;

    public WalletViewModel(ProfileViewModel profile, WalletSecrets secrets)
        : this(profile, secrets, startBackend: true)
    {
    }

    private WalletViewModel(ProfileViewModel profile, WalletSecrets secrets, bool startBackend)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _wallet = AppServices.Instance.CreateWalletService();
        Accounts.Add(new AccountChoice(0, AccountLabel(0)));
        _selectedAccount = Accounts[0];
        _nodeAddress = secrets.DaemonAddress;
        _renameText = secrets.Name;
        if (startBackend)
        {
            _ = InitializeAsync();
        }
    }

    /// <summary>A wallet screen with no backend behind it, inside the given preview profile — for
    /// UI snapshots and tests only.</summary>
    internal static WalletViewModel ForPreview(ProfileViewModel profile, WalletSecrets secrets) => new(profile, secrets, startBackend: false);

    /// <summary>A wallet screen with no backend, alone in a preview profile — for UI snapshots and tests.</summary>
    internal static WalletViewModel ForPreview(WalletSecrets secrets) =>
        ProfileViewModel.ForPreview(WalletProfile.OfOne(secrets)).Start();

    /// <summary>The vault profile this wallet belongs to (switcher, contacts, lock countdown).</summary>
    public ProfileViewModel Profile => _profile;

    public string WalletId => _secrets.Id;

    public string WalletName => _secrets.Name;

    /// <summary>A watch-only wallet sees incoming payments but can't spend.</summary>
    public bool IsViewOnly => _secrets.Kind == WalletKind.ViewOnly;

    public bool CanSpend => _secrets.CanSpend;

    internal void OnRenamed()
    {
        OnPropertyChanged(nameof(WalletName));
        RenameText = _secrets.Name;
    }

    /// <summary>On screen: refresh now, then at the normal pace.</summary>
    internal void OnForeground()
    {
        _foreground = true;
        _nextRefreshUtc = DateTime.UtcNow;
    }

    /// <summary>Another wallet is on screen: keep syncing, but slowly.</summary>
    internal void OnBackground()
    {
        _foreground = false;
        ShowManage = false;
    }

    // ------------------------------------------------------------------ status, balance, sync

    [ObservableProperty] private string _status = "Starting wallet…";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _startupFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockedBalance))]
    [NotifyPropertyChangedFor(nameof(HasLocked))]
    [NotifyPropertyChangedFor(nameof(BalanceDisplay))]
    [NotifyPropertyChangedFor(nameof(BalanceWhole))]
    [NotifyPropertyChangedFor(nameof(BalanceFraction))]
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

    partial void OnHeightChanged(ulong oldValue, ulong newValue)
    {
        // Confirmation counts only change for rows still pending or confirming. Rebuilding on every
        // height change would reset the list's scroll position every few seconds while syncing.
        // Judge "still confirming" at the OLD height: the change that takes a row from 9/10 to
        // settled is exactly the one that must redraw it.
        if (_entries.Any(e => e.Height == 0 || e.Type is "pool" or "pending" || oldValue < e.Height + HistoryRow.ConfirmationsToUnlock(e)))
        {
            RebuildHistoryRows();
        }
    }

    // Node sync tracker
    [ObservableProperty] private double _syncProgress;          // 0..100
    [ObservableProperty] private ulong _daemonHeight;
    [ObservableProperty] private bool _isSynced;
    [ObservableProperty] private string _syncText = "Connecting to node…";

    /// <summary>Masks balances on screen (shoulder-surfing). Persisted in settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceDisplay))]
    [NotifyPropertyChangedFor(nameof(BalanceWhole))]
    [NotifyPropertyChangedFor(nameof(BalanceFraction))]
    [NotifyPropertyChangedFor(nameof(UnlockedDisplay))]
    [NotifyPropertyChangedFor(nameof(LockedDisplay))]
    private bool _hideBalances = AppServices.Instance.Settings.HideBalances;

    partial void OnHideBalancesChanged(bool value) => RebuildHistoryRows(); // history amounts mask too

    private const string Masked = "●●●●●";

    public string BalanceDisplay => HideBalances ? Masked : XmrAmount.Format(Balance);

    /// <summary>Balance split for display: whole part bright, fraction dimmed ("12" + ".4830…").</summary>
    public string BalanceWhole => HideBalances ? Masked : decimal.Truncate(Balance).ToString(CultureInfo.InvariantCulture);

    public string BalanceFraction
    {
        get
        {
            if (HideBalances)
            {
                return string.Empty;
            }

            string full = XmrAmount.Format(Balance);
            int dot = full.IndexOf('.');
            return dot < 0 ? string.Empty : full[dot..];
        }
    }

    /// <summary>A watch-only wallet can't tell which of its outputs were spent (that takes the spend
    /// key), so what it shows is what it received: say so instead of calling it a balance.</summary>
    public string BalanceLabel => IsViewOnly ? "RECEIVED · SPENDS NOT VISIBLE" : "BALANCE";

    private string SpendableWord => IsViewOnly ? "Unlocked" : "Spendable";

    public string UnlockedDisplay => HideBalances ? $"{SpendableWord} {Masked}" : $"{SpendableWord} {XmrAmount.Format(UnlockedBalance)} XMR";

    public string LockedDisplay => HideBalances ? $"Maturing {Masked}" : $"Maturing {XmrAmount.Format(LockedBalance)} XMR";

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
    /// mainnet wallet is never mistaken for a test one (or vice versa). A local regtest node uses
    /// mainnet-format addresses but holds no real funds, so it gets its own label.</summary>
    public string NetworkLabel => IsLocalTestChain ? "Regtest · local test chain" : _secrets.Network.ToString();

    public bool IsMainnetWallet => _secrets.Network == MoneroNetwork.Mainnet && !IsLocalTestChain;

    /// <summary>The node is the user's own private test chain (see MoneroDiagnostics.IsLocalTestChainAsync).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkLabel))]
    [NotifyPropertyChangedFor(nameof(IsMainnetWallet))]
    private bool _isLocalTestChain;

    /// <summary>Which tab is showing: 0 Receive, 1 Send, 2 History, 3 Contacts, 4 Tools.</summary>
    [ObservableProperty] private int _selectedTab;

    // ------------------------------------------------------------------ accounts

    public ObservableCollection<AccountChoice> Accounts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountIndex))]
    private AccountChoice? _selectedAccount;

    /// <summary>The account balance, receive, send and history work with.</summary>
    public uint AccountIndex => SelectedAccount?.Index ?? 0;

    public bool HasSeveralAccounts => Accounts.Count > 1;

    partial void OnSelectedAccountChanged(AccountChoice? value)
    {
        if (value is null)
        {
            return;
        }

        // Everything shown is per account: start over from the new one.
        _entries = Array.Empty<TransferEntry>();
        History.Clear();
        HasHistory = false;
        Addresses.Clear();
        ReceiveAddress = string.Empty;
        ReceiveNotice = string.Empty;
        _nextRefreshUtc = DateTime.UtcNow;
        _ = SoftRefreshAsync();
    }

    private string AccountLabel(uint index) => _secrets.AccountLabels.GetValueOrDefault(index) ?? string.Empty;

    /// <summary>Bring the account list in line with the wallet (accounts appear as payments to them are
    /// found) and show each one's balance.</summary>
    private async Task RefreshAccountsAsync(CancellationToken ct)
    {
        GetAccountsResult accounts = await _wallet.GetAccountsAsync(ct);
        foreach (AccountInfo a in accounts.Accounts.OrderBy(a => a.AccountIndex))
        {
            AccountChoice? choice = Accounts.FirstOrDefault(c => c.Index == a.AccountIndex);
            if (choice is null)
            {
                choice = new AccountChoice(a.AccountIndex, AccountLabel(a.AccountIndex));
                Accounts.Add(choice);
                OnPropertyChanged(nameof(HasSeveralAccounts));
            }

            choice.BalanceText = HideBalances ? Masked : XmrAmount.Format(a.Balance) + " XMR";
        }
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Raised when the user asks for Settings from this screen (e.g. the startup-failure banner).</summary>
    [RelayCommand]
    private void OpenSettings() => _profile.RequestSettings();

    private async Task InitializeAsync()
    {
        StartupFailed = false;
        IsReady = false;
        try
        {
            Status = _secrets.Kind == WalletKind.Seed ? "Restoring wallet from seed…" : "Restoring wallet from its keys…";
            await _wallet.OpenAsync(_secrets, _cts.Token);
            IsLocalTestChain = _wallet.IsLocalTestChain;
            _profile.NoteLocalTestChain(WalletId, IsLocalTestChain);
            PrimaryAddress = await _wallet.GetPrimaryAddressAsync(_cts.Token);
            ReceiveAddress = PrimaryAddress;
            IsReady = true;
            Status = "Syncing in the background…";

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

    private async Task AutoRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= _nextRefreshUtc)
                {
                    await SoftRefreshAsync();

                    // Snappy while the node catches up; relaxed once synced; slow in the background.
                    int delayMs = !_foreground ? 60_000
                        : IsSynced ? Math.Clamp(AppServices.Instance.AutoRefreshSeconds, 5, 600) * 1000
                        : 3000;
                    _nextRefreshUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
                }

                await Task.Delay(1000, ct);
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

    /// <summary>Non-blocking refresh: reads current balance/height/history. Errors are soft.</summary>
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
            uint account = AccountIndex;
            (decimal balance, decimal unlocked) = await _wallet.GetBalanceAsync(account, _cts.Token);
            if (account != AccountIndex)
            {
                return; // the account changed meanwhile; the next refresh shows the right one
            }

            (Balance, UnlockedBalance) = (balance, unlocked);
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

            try
            {
                await RefreshAccountsAsync(_cts.Token);
                await RefreshAddressesAsync(_cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Info("Address list not refreshed: " + ex.GetType().Name);
            }

            // History often isn't available until the wallet finishes scanning; a transient
            // get_transfers failure here should NOT flip the sync status. Keep last known list.
            try
            {
                IReadOnlyList<TransferEntry> entries = await _wallet.GetHistoryAsync(account, _cts.Token);

                // Rebuild only when something changed: rebuilding on every refresh reset the scroll
                // position (and any selection) every 20 seconds.
                if (account == AccountIndex && !SameHistory(_entries, entries))
                {
                    SetHistory(entries);
                }
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

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private bool _historyNotReadyLogged;

    private void UpdateSyncStatus()
    {
        ulong wallet = Height;
        ulong node = DaemonHeight;

        if (node == 0)
        {
            IsSynced = false;
            SyncProgress = 0;
            SyncText = "Connecting to node…";
            Status = SyncText;
            return;
        }

        if (wallet + 1 >= node)
        {
            IsSynced = true;
            SyncProgress = 100;
            SyncText = $"Synced · block {node.ToString("N0", Inv)}";
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
            SyncText = $"Syncing · {SyncProgress.ToString("0.0", Inv)}%  ·  {wallet.ToString("N0", Inv)} / {node.ToString("N0", Inv)}  ({behind.ToString("N0", Inv)} behind)";
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
            Status = "Refreshing…";
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

    /// <summary>Locks the whole vault (every wallet), whichever one is showing.</summary>
    [RelayCommand]
    private Task LockAsync() => _profile.LockAsync();

    // ------------------------------------------------------------------ feedback to the view

    /// <summary>The view copies (with the clipboard auto-clear) and confirms with a toast.</summary>
    public event Action<string, string>? CopyRequested;

    /// <summary>A short message at the bottom of the window.</summary>
    public event Action<string, bool>? ToastRequested;

    private void Copy(string? text, string what) => CopyRequested?.Invoke(text ?? string.Empty, what);

    private void Toast(string text, bool ok = true) => ToastRequested?.Invoke(text, ok);

    // ------------------------------------------------------------------ errors

    private static string Friendly(Exception ex) => ex switch
    {
        FileNotFoundException => "monero-wallet-rpc was not found at a full path. Set its full path in Settings.",
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

        if (Has(m, "watch-only") || Has(m, "watch only"))
        {
            return "This is a watch-only wallet: it can't spend or sign.";
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

        WipeRevealedSecrets();
        _refreshGate.Dispose();
        _cts.Dispose();
    }
}

/// <summary>What happened to the last send attempt, for how the result is shown.</summary>
public enum SendOutcome
{
    None,

    /// <summary>Broadcast to the node.</summary>
    Sent,

    /// <summary>May or may not have reached the network: check History before trying again.</summary>
    NeedsCheck,

    /// <summary>Nothing was sent (invalid input, or building the transaction failed).</summary>
    Failed,
}
