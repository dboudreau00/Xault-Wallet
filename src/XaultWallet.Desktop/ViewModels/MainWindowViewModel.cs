using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Companion;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;
using XaultWallet.Desktop.Controls;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>A payment request that came from outside the app: a monero: link, or the browser companion.</summary>
/// <param name="Origin">The page the companion says it came from (scheme://host[:port]), or empty.</param>
public sealed record OutsidePayment(string Uri, CompanionCaller Via, string Origin)
{
    /// <summary>Where it came from, as the Send tab says it. A plain http page is shown with its scheme and
    /// port, and marked: anyone on the network path could have changed what it showed.</summary>
    public string Source => Via == CompanionCaller.Link
        ? "a monero: link"
        : CompanionProtocol.IsInsecureOrigin(Origin)
            ? "your browser (" + Origin + ", not encrypted)"
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

    /// <summary>It would have replaced what the user is doing in the Send tab: it waits behind "Open it".</summary>
    Held,

    /// <summary>A payment is being confirmed or sent: nothing was changed.</summary>
    Confirming,

    /// <summary>The Send form holds a payment the user started (typed, pasted or picked): nothing was changed.</summary>
    FormInUse,

    /// <summary>The last payment may have gone out and waits to be checked: nothing was changed.</summary>
    CheckLastPayment,

    /// <summary>The wallet on screen is watch-only: it can't pay.</summary>
    WatchOnly,

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

    /// <summary>A payment request waiting for the wallet to be on screen (after an unlock, or once Settings
    /// closes), since when (monotonic milliseconds), and the wallet that was open when it came (none while
    /// locked: then whichever wallet opens takes it).</summary>
    private OutsidePayment? _pendingPayment;

    private long _pendingSince;
    private WalletViewModel? _pendingWallet;

    /// <summary>A request (waiting or held) older than this is dropped rather than opened: by then it is no
    /// longer what the user just clicked.</summary>
    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

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

            // A held request names its page: it doesn't stay on the lock screen. A waiting one stays
            // (the lock screen says only that one waits) and goes to whichever wallet opens next.
            ClearHeld();
            DismissReceiveRequest(); // it names its site too
            _pendingWallet = null;

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

    /// <summary>The wallet on screen takes a payment request that was waiting for it, once. One that waited
    /// too long, or came while another wallet was open, is dropped and the user told why.</summary>
    partial void OnCurrentChanged(ViewModelBase value)
    {
        if (value is not WalletViewModel wallet || _pendingPayment is not { } pending || !ReferenceEquals(_profile?.Active, wallet))
        {
            return;
        }

        WalletViewModel? meantFor = _pendingWallet;
        _pendingPayment = null;
        _pendingWallet = null;
        if (Expired(_pendingSince))
        {
            ShowNotice($"A payment request from {pending.Source} waited too long and was dropped. Open it again if you still want to pay it.", warning: false);
            return;
        }

        if (meantFor is not null && !ReferenceEquals(meantFor, wallet))
        {
            ShowNotice($"A payment request from {pending.Source} came while another wallet was open, so it wasn't put in this one. Open it again from where it came.", warning: false);
            return;
        }

        try
        {
            if (Deliver(wallet, pending) is (PaymentOffer.Confirming or PaymentOffer.WatchOnly) and var refused)
            {
                PaymentNotOpened(pending, refused);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Opening a waiting payment request failed", ex);
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

    /// <summary>A payment request from a monero: link or the browser companion: into the Send tab now if
    /// the wallet is on screen (held behind "Open it" when that would replace what the user is doing
    /// there), else when it is (after an unlock, or once Settings closes).</summary>
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
            return Deliver(wallet, payment);
        }

        SetPending(payment);
        return PaymentOffer.Waiting;
    }

    /// <summary>Into the wallet's Send form, or held behind "Open it" when it would replace a form the user
    /// filled or a payment to check. Confirming and WatchOnly come back as refusals for the caller to report.</summary>
    private PaymentOffer Deliver(WalletViewModel wallet, OutsidePayment payment)
    {
        PaymentOffer offer = wallet.ApplyPaymentRequest(payment);
        if (offer is PaymentOffer.FormInUse or PaymentOffer.CheckLastPayment)
        {
            Hold(wallet, payment, offer);
            return PaymentOffer.Held;
        }

        return offer;
    }

    private void SetPending(OutsidePayment payment)
    {
        if (_pendingPayment is not null)
        {
            ShowNotice("A newer payment request replaced one that was waiting.", warning: false);
        }

        _pendingPayment = payment;
        _pendingSince = Environment.TickCount64;
        _pendingWallet = _profile?.Active;
        if (Current is UnlockViewModel unlock)
        {
            unlock.PendingRequestNote = PendingNote();
        }
    }

    /// <summary>The lock screen says that a request waits, and nothing about it: anyone at the computer can read it.</summary>
    private string PendingNote() => _pendingPayment is not null
        ? "A payment request is waiting. Unlock to review it in the Send tab."
        : string.Empty;

    private static bool Expired(long since) => Environment.TickCount64 - since > (long)PendingLifetime.TotalMilliseconds;

    /// <summary>The launch's own monero: link (the app was started to open it).</summary>
    internal void OfferLaunchLink(string uri)
    {
        var payment = new OutsidePayment(uri, CompanionCaller.Link, string.Empty);
        if (OfferPayment(payment) is (PaymentOffer.Confirming or PaymentOffer.WatchOnly) and var refused)
        {
            PaymentNotOpened(payment, refused);
        }
    }

    /// <summary>A request the wallet couldn't take: the app says so (a link click has no other answer).</summary>
    internal void PaymentNotOpened(OutsidePayment payment, PaymentOffer why)
    {
        string reason = why switch
        {
            PaymentOffer.Confirming => "a payment was being confirmed",
            PaymentOffer.WatchOnly => "the wallet that is open is watch-only",
            _ => "it couldn't be shown",
        };
        ShowNotice($"A payment request from {payment.Source} wasn't opened: {reason}. Open it again once you're ready.", warning: false);
    }

    internal CompanionStatus StatusForCompanion()
    {
        if (_profile is { Active: { } wallet })
        {
            return new CompanionStatus("unlocked", wallet.CompanionNetwork, wallet.IsViewOnly, wallet.IsReady);
        }

        return new CompanionStatus(VaultManager.Exists(AppServices.Instance.VaultPath) ? "locked" : "setup", null, false, false);
    }

    /// <summary>A new receive address of the wallet on screen. Only while the wallet itself is showing, and
    /// only after the user allowed it in the app ("Give address"): the first request just asks. The
    /// app announces it across the top of the window with its start and end, for the user to compare
    /// with what the browser shows.</summary>
    internal async Task<CompanionAddress> AddressForCompanionAsync(string label, string origin)
    {
        if (_profile is not { Active: { } wallet })
        {
            return new CompanionAddress(null, string.Empty, VaultManager.Exists(AppServices.Instance.VaultPath)
                ? new CompanionError(CompanionErrors.Locked, "Unlock XaultWallet to get an address.")
                : new CompanionError(CompanionErrors.NoVault, "XaultWallet has no wallet yet. Set it up first."));
        }

        if (!ReferenceEquals(Current, wallet))
        {
            return new CompanionAddress(null, string.Empty, new CompanionError(CompanionErrors.Busy, "Open the wallet in XaultWallet (close Settings or the screen that is open), then ask again."));
        }

        if (!wallet.IsReady)
        {
            return new CompanionAddress(null, string.Empty, new CompanionError(CompanionErrors.NotReady, "The wallet on screen is still starting. Try again in a moment."));
        }

        string host = CompanionProtocol.HostOf(origin);
        if (!_receiveGate.TryTake(wallet, host))
        {
            ReceiveAskHold.Start(); // the window comes forward with it: a click already on its way can't press Give address
            ReceiveAskNote = $"Your browser asks for a new receive address{(host.Length > 0 ? " for " + host : string.Empty)}. "
                             + "Give it one, then ask again in the browser within two minutes."
                             + (AppServices.Instance.Settings.CompanionSavesSiteNames ? string.Empty : " It will be labelled just \"Browser\".");
            return new CompanionAddress(null, string.Empty, new CompanionError(CompanionErrors.Busy,
                "Confirm in XaultWallet (Give address), then ask again here."));
        }

        if (await wallet.CreateAddressForCompanionAsync(label, origin, AppServices.Instance.Settings.CompanionSavesSiteNames) is not { } address)
        {
            return new CompanionAddress(null, string.Empty, new CompanionError(CompanionErrors.Failed, "XaultWallet couldn't make a new address. Its Receive tab says why."));
        }

        ShowNotice($"New address given to your browser{(host.Length > 0 ? " for " + host : string.Empty)}: {address[..8]}…{address[^8..]}. The extension should show the same start and end.", warning: false);
        return new CompanionAddress(address, wallet.CompanionNetwork, null);
    }

    // ------------------------------------------------------------------ a receive request behind "Give address"

    /// <summary>A browser's request for a new address waits for the user (see <see cref="CompanionReceiveGate"/>).</summary>
    private readonly CompanionReceiveGate _receiveGate = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReceiveAsk))]
    private string _receiveAskNote = string.Empty;

    public bool HasReceiveAsk => ReceiveAskNote.Length > 0;

    /// <summary>"Give address" ignores clicks for a moment after the request lands (as "Send now" does).</summary>
    private ClickHold? _receiveAskHold;

    private ClickHold ReceiveAskHold => _receiveAskHold ??= new ClickHold(() => OnPropertyChanged(nameof(ReceiveAskHeld)));

    public bool ReceiveAskHeld => _receiveAskHold?.Held ?? false;

    /// <summary>The user allows the waiting request (with Enhanced Security on, only the user's own input can,
    /// not another program's): the browser's next request from that site gets its address.</summary>
    [RelayCommand]
    private void AllowReceiveRequest()
    {
        if (ReceiveAskHeld)
        {
            return; // it only just appeared
        }

        string? host = _receiveGate.AskedHost;
        ReceiveAskNote = string.Empty;
        ShowNotice(_receiveGate.Allow()
            ? $"Allowed: ask for the address again in your browser{(host is { Length: > 0 } ? " on " + host : string.Empty)} within two minutes."
            : "The browser's request waited too long and was dropped. Ask again from the browser.", warning: false);
    }

    [RelayCommand]
    private void DismissReceiveRequest()
    {
        _receiveGate.Clear();
        ReceiveAskNote = string.Empty;
    }

    // ------------------------------------------------------------------ a request held behind "Open it"

    /// <summary>A payment request that would have replaced what the user is doing in the Send tab (a form
    /// they filled, a payment to check): it waits until the user opens it (with Enhanced Security on, only the
    /// user's own input can, not another program's) or dismisses it, for the wallet it came for.</summary>
    private OutsidePayment? _heldPayment;

    private WalletViewModel? _heldWallet;
    private long _heldSince;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeldRequest))]
    private string _heldRequestNote = string.Empty;

    public bool HasHeldRequest => HeldRequestNote.Length > 0;

    private void Hold(WalletViewModel wallet, OutsidePayment payment, PaymentOffer why)
    {
        bool replacing = _heldPayment is not null;
        (_heldPayment, _heldWallet, _heldSince) = (payment, wallet, Environment.TickCount64);
        string reason = why == PaymentOffer.CheckLastPayment
            ? "the Send tab shows a payment that may already have gone out"
            : "the Send form holds a payment you started";
        HeldRequestNote = $"A payment request from {payment.Source} is waiting, because {reason}. Open it to put it in the Send form in place of that."
                          + (replacing ? " It replaced an earlier request." : string.Empty);
    }

    private void ClearHeld()
    {
        _heldPayment = null;
        _heldWallet = null;
        HeldRequestNote = string.Empty;
    }

    [RelayCommand]
    private void OpenHeldRequest()
    {
        if (_heldPayment is not { } held)
        {
            return;
        }

        if (Expired(_heldSince))
        {
            ClearHeld();
            ShowNotice($"A payment request from {held.Source} waited too long and was dropped. Open it again if you still want to pay it.", warning: false);
            return;
        }

        if (_heldWallet is not { } wallet || !ReferenceEquals(Current, wallet) || !ReferenceEquals(_profile?.Active, wallet))
        {
            ShowNotice("Go back to the wallet the request came for (close Settings or the screen that is open), then open it.", warning: false);
            return; // it stays held
        }

        ClearHeld();
        if (wallet.ApplyPaymentRequest(held, userChose: true) is not PaymentOffer.Shown and var refused)
        {
            PaymentNotOpened(held, refused);
        }
    }

    [RelayCommand]
    private void DismissHeldRequest() => ClearHeld();

    // ------------------------------------------------------------------ notices (top of the window)

    /// <summary>A message across the top of every screen: what the app noticed about its own setup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = string.Empty;

    [ObservableProperty] private bool _noticeIsWarning;

    /// <summary>The notice is about something Settings can fix: it offers to open it.</summary>
    [ObservableProperty] private bool _noticeOffersSettings;

    public bool HasNotice => Notice.Length > 0;

    /// <summary>Notices waiting behind the one showing (one at a time; a warning goes first).</summary>
    private readonly List<(string Text, bool Warning, bool OffersSettings)> _queuedNotices = new();

    internal void ShowNotice(string text, bool warning, bool offersSettings = false)
    {
        if (text == Notice || _queuedNotices.Any(n => n.Text == text))
        {
            return;
        }

        if (HasNotice && (NoticeIsWarning || !warning))
        {
            _queuedNotices.Add((text, warning, offersSettings));
            return;
        }

        if (HasNotice)
        {
            _queuedNotices.Insert(0, (Notice, NoticeIsWarning, NoticeOffersSettings)); // a warning goes in front
        }

        (NoticeIsWarning, NoticeOffersSettings, Notice) = (warning, offersSettings, text);
    }

    /// <summary>A notice that no longer applies goes, showing or waiting.</summary>
    private void RemoveNotice(string text)
    {
        _queuedNotices.RemoveAll(n => n.Text == text);
        if (Notice == text)
        {
            NextNotice();
        }
    }

    private void NextNotice()
    {
        if (_queuedNotices.Count == 0)
        {
            Notice = string.Empty;
            return;
        }

        (string text, bool warning, bool offersSettings) = _queuedNotices[0];
        _queuedNotices.RemoveAt(0);
        (NoticeIsWarning, NoticeOffersSettings, Notice) = (warning, offersSettings, text);
    }

    [RelayCommand]
    private void DismissNotice() => NextNotice();

    [RelayCommand]
    private void ReviewNotice()
    {
        NextNotice();
        OpenSettings();
    }

    /// <summary>Tripwire notices already shown this run (a check repeated when the window comes back to
    /// the front doesn't repeat one the user dismissed).</summary>
    private readonly HashSet<string> _tripwireShown = new(StringComparer.Ordinal);

    /// <summary>What the tripwire read: each watched browser's registration, the link handler, and whether
    /// Windows' own choice of app for monero: links outranks it.</summary>
    private sealed record RegistrationReport(List<(string Browser, string Key, RegistrationCheck Check)> Browsers, RegistrationState Links, bool LinksOverridden);

    /// <summary>
    /// The tripwire: every browser connection and the monero: link handler this app set up must still lead
    /// to it. A change made behind its back (by another program) is how something could stand in for
    /// XaultWallet and hand the browser its own addresses, so the user is warned, with the program it now
    /// leads to. One that was removed (an uninstall does that) is mentioned once, calmly, then forgotten.
    /// The files and the registry are read off the UI thread, with a time limit. Runs at start and again
    /// when the window comes back to the front.
    /// </summary>
    internal async Task CheckRegistrationsAsync()
    {
        if (AppServices.ExecutablePath is not { } exe)
        {
            return;
        }

        AppServices app = AppServices.Instance;
        string[] connected = app.Settings.ConnectedBrowsers.ToArray();
        bool links = app.Settings.OpensMoneroLinks;
        RegistrationReport report;
        try
        {
            report = await Task.Run(() => Inspect(exe, connected, links, app)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Tripwire("XaultWallet couldn't finish checking its browser connections: reading them took too long. Review them in Settings.", warning: true);
            return;
        }

        var gone = new List<string>();
        foreach ((string browser, string key, RegistrationCheck check) in report.Browsers)
        {
            if (check.State == RegistrationState.Elsewhere)
            {
                Tripwire(Changed(browser, check.Target), warning: true);
            }
            else if (check.State == RegistrationState.NotRegistered)
            {
                gone.Add(browser);
                app.Settings.ConnectedBrowsers.Remove(key);
            }
        }

        if (links)
        {
            switch (report.Links)
            {
                case RegistrationState.Elsewhere:
                    Tripwire("monero: links no longer open with this XaultWallet. Installing another wallet can do that; if you didn't, review it in Settings.", warning: true);
                    break;
                case RegistrationState.NotRegistered:
                    gone.Add("monero: links");
                    app.Settings.OpensMoneroLinks = false;
                    break;
                case RegistrationState.Registered when report.LinksOverridden:
                    Tripwire("Windows is set to open monero: links with another app, so the links you click don't reach XaultWallet. To change that: Windows Settings, Apps, Default apps.", warning: false);
                    break;
            }
        }

        if (gone.Count > 0)
        {
            TrySaveSettings();
            Tripwire($"XaultWallet's connection for {string.Join(" and ", gone)} was removed. If you didn't do that, check Settings.", warning: false);
        }
    }

    private static RegistrationReport Inspect(string exe, string[] connected, bool links, AppServices app)
    {
        var browsers = connected.Select(key =>
        {
            CompanionBrowser kind = key == "firefox" ? CompanionBrowser.Firefox : CompanionBrowser.Chrome;
            return (kind.ToString(), key, BrowserRegistration.Check(kind, exe, app.BrowserPaths, app.UserRegistry));
        }).ToList();
        RegistrationState linkState = links ? MoneroLinkRegistration.Status(exe, app.BrowserPaths, app.UserRegistry) : RegistrationState.Unsupported;
        bool overridden = linkState == RegistrationState.Registered && MoneroLinkRegistration.WindowsUserChoiceOverrides(app.UserRegistry);
        return new RegistrationReport(browsers, linkState, overridden);
    }

    private void Tripwire(string text, bool warning)
    {
        if (_tripwireShown.Add(text))
        {
            ShowNotice(text, warning, offersSettings: true);
        }
    }

    /// <summary>What a browser connection that leads elsewhere now leads to, in words: a program that is no
    /// longer there, another copy of XaultWallet, or something else.</summary>
    private static string Changed(string browser, string? target)
    {
        const string StandIn = "If you didn't do that, another program may be trying to stand in for XaultWallet. Review it in Settings.";
        if (target is null)
        {
            return $"Your {browser} connection to XaultWallet has been changed and no longer leads to this app. {StandIn}";
        }

        string shown = UntrustedText.Clean(target, 120);
        bool local = Path.IsPathFullyQualified(target) && !target.StartsWith(@"\\", StringComparison.Ordinal);
        if (local && !File.Exists(target))
        {
            return $"Your {browser} connection leads to a program that is no longer there ({shown}). Connect it again in Settings, so nothing put there later can answer in XaultWallet's place.";
        }

        return string.Equals(Path.GetFileName(target), Path.GetFileName(AppServices.ExecutablePath), StringComparison.OrdinalIgnoreCase)
            ? $"Your {browser} connection now leads to another copy of XaultWallet ({shown}). If that isn't a copy you use, review it in Settings: another program may be trying to stand in for XaultWallet."
            : $"Your {browser} connection to XaultWallet now leads to another program ({shown}). {StandIn}";
    }

    /// <summary>The channel for the browser and links couldn't be opened: its name was taken.</summary>
    internal void CompanionNameTaken() =>
        ShowNotice("Another program holds XaultWallet's channel for the browser and monero: links, so both are off until XaultWallet restarts. " +
                   "If no other copy of XaultWallet is running, something on this computer may be trying to stand in for it.", warning: true);

    /// <summary>The channel can't be made on this system (Linux, macOS: the path to the data folder is too long for a socket).</summary>
    internal void CompanionUnavailable() =>
        ShowNotice("The browser companion and monero: links are off: the path to XaultWallet's data folder is too long for their channel on this system.", warning: false);

    /// <summary>At start: the settings file couldn't be read, so the defaults were used.</summary>
    internal void SettingsWereDamaged() =>
        ShowNotice("XaultWallet's settings file was damaged, so it started with the defaults (the damaged file is kept beside it with .bad added to its name). " +
                   "Check Settings: Enhanced Security, if you had it on, is off.", warning: true, offersSettings: true);

    // ------------------------------------------------------------------ Enhanced Security

    /// <summary>Turns the window protections on or off (set by the app; the window is its business).</summary>
    internal Action<bool>? ApplyEnhancedSecurity { get; set; }

    /// <summary>What turning it on achieved on this computer (set by the app, read for the notice).</summary>
    internal Func<string?>? EnhancedSecurityShortfall { get; set; }

    /// <summary>Just turned on: until the user clicks "Keep it on" (which only a real mouse, touchpad or
    /// key press can do now), it can still be undone by itself, and it isn't trusted at the next start.</summary>
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
                AppServices.Instance.Settings.EnhancedSecurityConfirmed = false;
                TrySaveSettings();
                if (Current is SettingsViewModel open)
                {
                    open.EnhancedSecurity = false; // the open Settings screen shows it off too
                }

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
            RemoveNotice(IgnoringInputNotice);
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
        AppServices.Instance.Settings.EnhancedSecurityConfirmed = true; // from now on it starts with the app
        TrySaveSettings();
        string? shortfall = EnhancedSecurityShortfall?.Invoke();
        ShowNotice(shortfall is not null
                ? "Enhanced Security is on, but not all of it works on this computer: " + shortfall
                : "Enhanced Security is on. Screenshots and screen sharing no longer show XaultWallet, and only your own keyboard, mouse or touchscreen can press its buttons. " +
                  "Restart XaultWallet to cover its menus and lists too.",
            warning: shortfall is not null);
    }

    /// <summary>At start: Enhanced Security was turned on last time but never confirmed (the app was
    /// closed during the countdown), so it was left off.</summary>
    internal void EnhancedSecurityNotConfirmed() =>
        ShowNotice("Enhanced Security is off: it was turned on last time, but XaultWallet closed before you clicked Keep it on. Turn it on again in Settings if you want it.",
            warning: true, offersSettings: true);

    /// <summary>Said when Enhanced Security has dropped several key presses or clicks and not one has come
    /// from a device since it was turned on; taken away by the first one that does.</summary>
    internal const string IgnoringInputNotice =
        "XaultWallet is ignoring input that didn't come from a device. If your own keyboard and mouse don't work, close XaultWallet from the taskbar and start it with --no-enhanced-security.";

    internal void EnhancedInputIgnored() => ShowNotice(IgnoringInputNotice, warning: true);

    internal void EnhancedInputConfirmed() => RemoveNotice(IgnoringInputNotice);

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
