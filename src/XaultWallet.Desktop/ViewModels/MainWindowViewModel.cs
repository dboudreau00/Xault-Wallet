using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Companion;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>A payment request that came from outside the app: a monero: link, or the browser companion.</summary>
/// <param name="Origin">The page the companion says it came from (scheme://host), or empty.</param>
public sealed record OutsidePayment(string Uri, CompanionCaller Via, string Origin)
{
    /// <summary>Where it came from, as the Send tab and the unlock screen say it.</summary>
    public string Source => Via == CompanionCaller.Link
        ? "a monero: link"
        : "your browser" + (CompanionProtocol.HostOf(Origin) is { Length: > 0 } host ? " (" + host + ")" : string.Empty);
}

/// <summary>What became of a payment request from outside.</summary>
public enum PaymentOffer
{
    /// <summary>It is in the Send tab now.</summary>
    Shown,

    /// <summary>The vault is locked: it opens in the Send tab after the next unlock.</summary>
    AfterUnlock,

    /// <summary>Settings (or adding a wallet) is open: it opens when the wallet is back on screen.</summary>
    Waiting,

    /// <summary>A payment is being confirmed: nothing was changed.</summary>
    Busy,

    /// <summary>There is no vault yet.</summary>
    NoVault,
}

/// <summary>The app's state as the companion may know it.</summary>
public sealed record CompanionStatus(string State, string? Network, bool WatchOnly, bool Ready);

/// <summary>A new receive address for the companion, or why there isn't one.</summary>
public sealed record CompanionAddress(string? Address, string Network, CompanionError? Error);

public sealed partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase _current;

    /// <summary>True while the Settings screen is showing (hides the gear, which would be a no-op).</summary>
    [ObservableProperty]
    private bool _settingsOpen;

    private ProfileViewModel? _profile;

    /// <summary>The last lock's remaining work (backends stopping, the last save, the session
    /// closing): nothing opens or replaces the vault until it has finished.</summary>
    private Task _closing = Task.CompletedTask;

    /// <summary>A payment request waiting for the wallet to be on screen (after an unlock, or once
    /// Settings closes).</summary>
    private OutsidePayment? _pendingPayment;

    /// <summary>A shell showing <paramref name="screen"/> with no startup flow — for UI snapshots and tests only.</summary>
    internal MainWindowViewModel(ViewModelBase screen) => _current = screen;

    public MainWindowViewModel()
    {
        // Show the startup splash first; it runs node + binary checks in the background,
        // then routes to create-wallet (no vault) or unlock (vault exists).
        var startup = new StartupViewModel();
        startup.Ready += () => Current = startup.VaultExists ? BuildUnlock() : BuildCreate();
        _current = startup;
    }

    private UnlockViewModel BuildUnlock()
    {
        var vm = new UnlockViewModel(_closing) { PendingRequestNote = PendingNote() };
        vm.Unlocked += OnUnlocked;
        vm.PendingRequestDismissed += () => _pendingPayment = null;
        return vm;
    }

    /// <summary>Forwarded from the window on any input; defers auto-lock while a wallet is open.</summary>
    public void NotifyActivity() => _profile?.NotifyActivity();

    private CreateWalletViewModel BuildCreate()
    {
        var vm = new CreateWalletViewModel();
        vm.Created += () => Current = BuildUnlock();
        return vm;
    }

    private void OnUnlocked(VaultSession session)
    {
        // The UI is identical whichever profile was opened — by construction: the vault returns the
        // same kind of profile for both slots and nothing downstream could tell them apart.
        var profile = new ProfileViewModel(session);
        _profile = profile;
        profile.ActiveChanged += vm =>
        {
            // A switch (or a wallet just added) comes on screen — unless Settings is showing.
            if (ReferenceEquals(_profile, profile) && !SettingsOpen)
            {
                Current = vm;
            }
        };
        profile.Locking += closing =>
        {
            // A manual Lock can race the inactivity lock: act once.
            if (!ReferenceEquals(_profile, profile))
            {
                return;
            }

            // The wallets leave the screen now, not after their backends have stopped: until then
            // the screen would stay usable, and a change made on it couldn't be saved any more.
            _profile = null;
            _closing = EndedAsync(closing);

            // If the auto-lock fires while Settings is open, close the Settings state too —
            // otherwise its Closed handler later re-navigates over whatever screen is showing
            // (and the gear button stays hidden on the unlock screen).
            SettingsOpen = false;
            Current = BuildUnlock();
        };
        profile.SettingsRequested += OpenSettings;
        profile.AddWalletRequested += () => OpenAddWallet(profile);
        Current = profile.Start();
    }

    /// <summary>The wallet on screen takes a payment request that was waiting for it.</summary>
    partial void OnCurrentChanged(ViewModelBase value)
    {
        if (value is WalletViewModel wallet && _pendingPayment is { } pending && ReferenceEquals(_profile?.Active, wallet)
            && wallet.ApplyPaymentRequest(pending) is null)
        {
            _pendingPayment = null;
        }
    }

    /// <summary>The add-wallet screen; it returns to the (new, or previous) wallet when done.</summary>
    private void OpenAddWallet(ProfileViewModel profile)
    {
        var add = new AddWalletViewModel(profile);
        add.Cancelled += () =>
        {
            if (ReferenceEquals(_profile, profile) && profile.Active is { } active)
            {
                Current = active;
            }
        };
        Current = add;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (SettingsOpen)
        {
            return;
        }

        ViewModelBase before = Current;
        var settings = new SettingsViewModel(_profile, _closing);
        settings.EnhancedSecurityChanged += OnEnhancedSecurityChanged;
        settings.Closed += () =>
        {
            SettingsOpen = false;

            // If a wallet is open, return to it (or to the screen Settings was opened from).
            // Otherwise rebuild the create/unlock screen fresh so it picks up any node/network
            // default just changed in Settings (the pre-settings instance would still hold the old default).
            if (_profile is { Active: { } active })
            {
                Current = before is AddWalletViewModel ? before : active;
            }
            else if (VaultManager.Exists(AppServices.Instance.VaultPath))
            {
                Current = BuildUnlock();
            }
            else
            {
                Current = BuildCreate();
            }
        };

        SettingsOpen = true;
        Current = settings;
    }

    /// <summary>Completes when <paramref name="work"/> has ended, whichever way: a lock that failed
    /// to finish must not keep the vault from being opened again.</summary>
    private static async Task EndedAsync(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception ex)
        {
            Log.Error("Locking did not finish cleanly", ex);
        }
    }

    public async Task ShutdownAsync()
    {
        if (_profile is not null)
        {
            await _profile.DisposeAsync();
        }

        await _closing; // a lock still finishing
        await AppServices.Instance.TemporaryBackends.StopAllAsync(); // e.g. a seed being generated
        await AppServices.Instance.Tor.StopAsync(); // last: wallets above may still be routed through it
    }

    // ------------------------------------------------------------------ browser companion and links

    /// <summary>A payment request from a monero: link or the browser companion: into the Send tab now
    /// if the wallet is on screen, else when it is (after an unlock, or once Settings closes).</summary>
    internal PaymentOffer OfferPayment(OutsidePayment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (!VaultManager.Exists(AppServices.Instance.VaultPath))
        {
            return PaymentOffer.NoVault;
        }

        if (_profile is null)
        {
            SetPending(payment);
            return PaymentOffer.AfterUnlock;
        }

        if (Current is WalletViewModel wallet && ReferenceEquals(_profile.Active, wallet))
        {
            return wallet.ApplyPaymentRequest(payment) is null ? PaymentOffer.Shown : PaymentOffer.Busy;
        }

        SetPending(payment);
        return PaymentOffer.Waiting;
    }

    private void SetPending(OutsidePayment payment)
    {
        _pendingPayment = payment;
        if (Current is UnlockViewModel unlock)
        {
            unlock.PendingRequestNote = PendingNote();
        }
    }

    private string PendingNote() => _pendingPayment is { } p
        ? $"A payment request from {p.Source} is waiting. Unlock to review it in the Send tab."
        : string.Empty;

    /// <summary>The launch's own monero: link (the app was started to open it).</summary>
    internal void OfferLaunchLink(string uri) => OfferPayment(new OutsidePayment(uri, CompanionCaller.Link, string.Empty));

    internal CompanionStatus StatusForCompanion()
    {
        if (_profile is { Active: { } wallet })
        {
            return new CompanionStatus("unlocked", wallet.CompanionNetwork, wallet.IsViewOnly, wallet.IsReady);
        }

        return new CompanionStatus(VaultManager.Exists(AppServices.Instance.VaultPath) ? "locked" : "setup", null, false, false);
    }

    internal async Task<CompanionAddress> AddressForCompanionAsync(string label, string origin)
    {
        if (_profile is not { Active: { } wallet })
        {
            return new CompanionAddress(null, string.Empty, VaultManager.Exists(AppServices.Instance.VaultPath)
                ? new CompanionError(CompanionErrors.Locked, "Unlock XaultWallet to get an address.")
                : new CompanionError(CompanionErrors.NoVault, "XaultWallet has no wallet yet. Set it up first."));
        }

        if (!wallet.IsReady)
        {
            return new CompanionAddress(null, string.Empty, new CompanionError(CompanionErrors.NotReady, "The wallet on screen is still starting. Try again in a moment."));
        }

        (string? address, string? error) = await wallet.CreateAddressForCompanionAsync(label, origin);
        return address is not null
            ? new CompanionAddress(address, wallet.CompanionNetwork, null)
            : new CompanionAddress(null, string.Empty, new CompanionError(CompanionErrors.Failed, error ?? "The address couldn't be created."));
    }

    // ------------------------------------------------------------------ notices (top of the window)

    /// <summary>A message across the top of every screen: what the app noticed about its own setup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = string.Empty;

    [ObservableProperty] private bool _noticeIsWarning;

    /// <summary>The notice is about something Settings can fix: it offers to open it.</summary>
    [ObservableProperty] private bool _noticeOffersSettings;

    public bool HasNotice => Notice.Length > 0;

    internal void ShowNotice(string text, bool warning, bool offersSettings = false)
    {
        Notice = text;
        NoticeIsWarning = warning;
        NoticeOffersSettings = offersSettings;
    }

    [RelayCommand]
    private void DismissNotice() => Notice = string.Empty;

    [RelayCommand]
    private void ReviewNotice()
    {
        Notice = string.Empty;
        OpenSettings();
    }

    /// <summary>
    /// The tripwire: every browser connection and the monero: link handler this app set up must still
    /// point at it. A change made behind its back (by another program) is how something could stand in
    /// for XaultWallet and hand the browser its own addresses, so the user is told.
    /// </summary>
    internal void CheckRegistrations()
    {
        AppServices app = AppServices.Instance;
        if (AppServices.ExecutablePath is not { } exe)
        {
            return;
        }

        // Pointing elsewhere is what an impostor needs; a connection that is simply gone (an uninstall,
        // or removed by hand) only means it was disconnected, and is forgotten without a fuss.
        var changed = new List<string>();
        bool forgot = false;
        foreach (string browser in app.Settings.ConnectedBrowsers.ToList())
        {
            CompanionBrowser kind = browser == "firefox" ? CompanionBrowser.Firefox : CompanionBrowser.Chrome;
            switch (BrowserRegistration.Status(kind, exe, app.BrowserPaths, app.UserRegistry))
            {
                case RegistrationState.Elsewhere:
                    changed.Add(kind.ToString());
                    break;
                case RegistrationState.NotRegistered:
                    app.Settings.ConnectedBrowsers.Remove(browser);
                    forgot = true;
                    break;
            }
        }

        RegistrationState links = app.Settings.OpensMoneroLinks ? MoneroLinkRegistration.Status(exe, app.BrowserPaths, app.UserRegistry) : RegistrationState.NotRegistered;
        if (app.Settings.OpensMoneroLinks && links == RegistrationState.NotRegistered)
        {
            app.Settings.OpensMoneroLinks = false;
            forgot = true;
        }

        if (forgot)
        {
            TrySaveSettings();
        }

        if (changed.Count > 0)
        {
            ShowNotice($"Your {string.Join(" and ", changed)} connection to XaultWallet has been changed and no longer leads to this app. " +
                       "If you didn't do that, another program may be trying to stand in for XaultWallet. Review it in Settings.", warning: true, offersSettings: true);
            return;
        }

        if (links == RegistrationState.Elsewhere)
        {
            ShowNotice("monero: links no longer open with this XaultWallet. Installing another wallet can do that; if you didn't, review it in Settings.",
                warning: true, offersSettings: true);
        }
    }

    /// <summary>The channel for the browser and links couldn't be opened: its name was taken.</summary>
    internal void CompanionNameTaken() =>
        ShowNotice("Another program holds XaultWallet's channel for the browser and monero: links, so both are off until XaultWallet restarts. " +
                   "If no other copy of XaultWallet is running, something on this computer may be trying to stand in for it.", warning: true);

    // ------------------------------------------------------------------ Enhanced Security

    /// <summary>Turns the window protections on or off (set by the app; the window is its business).</summary>
    internal Action<bool>? ApplyEnhancedSecurity { get; set; }

    /// <summary>Just turned on: until the user clicks "Keep it on" (which only a real mouse, touchpad or
    /// key press can do now), it can still be undone by itself.</summary>
    [ObservableProperty] private bool _confirmingEnhancedSecurity;

    [ObservableProperty] private string _enhancedSecurityCountdown = string.Empty;

    internal const int EnhancedSecurityConfirmSeconds = 20;

    private DispatcherTimer? _enhancedTimer;
    private int _enhancedSecondsLeft;

    private void OnEnhancedSecurityChanged(bool on)
    {
        if (on)
        {
            ApplyEnhancedSecurity?.Invoke(true);
            _enhancedSecondsLeft = EnhancedSecurityConfirmSeconds;
            UpdateEnhancedCountdown();
            ConfirmingEnhancedSecurity = true;
            _enhancedTimer?.Stop();
            _enhancedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _enhancedTimer.Tick += (_, _) =>
            {
                if (--_enhancedSecondsLeft > 0)
                {
                    UpdateEnhancedCountdown();
                    return;
                }

                // Nothing got through: the filter may not be seeing this computer's devices as devices.
                // Back off rather than leave the user unable to click.
                _enhancedTimer?.Stop();
                ConfirmingEnhancedSecurity = false;
                ApplyEnhancedSecurity?.Invoke(false);
                AppServices.Instance.Settings.EnhancedSecurity = false;
                TrySaveSettings();
                ShowNotice("Enhanced Security was turned off again: XaultWallet didn't get your click while it was on. " +
                           "Try again with the mouse, touchpad or keyboard you normally use, or leave it off on this computer.", warning: true, offersSettings: true);
            };
            _enhancedTimer.Start();
        }
        else
        {
            _enhancedTimer?.Stop();
            ConfirmingEnhancedSecurity = false;
            ApplyEnhancedSecurity?.Invoke(false);
            ShowNotice(ProcessHardening.Enabled
                ? "Enhanced Security is off. The memory protection it turned on stays until XaultWallet restarts."
                : "Enhanced Security is off.", warning: false);
        }
    }

    private void UpdateEnhancedCountdown() =>
        EnhancedSecurityCountdown = $"Enhanced Security is on. Click Keep it on within {_enhancedSecondsLeft} s to confirm XaultWallet still gets your clicks, or it turns itself off.";

    [RelayCommand]
    private void KeepEnhancedSecurity()
    {
        _enhancedTimer?.Stop();
        ConfirmingEnhancedSecurity = false;
        ShowNotice("Enhanced Security is on. Screenshots and remote viewing no longer show XaultWallet, and only your own keyboard, mouse or touchscreen can operate it.", warning: false);
    }

    private static void TrySaveSettings()
    {
        try
        {
            AppServices.Instance.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings failed", ex);
        }
    }
}
