using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop.ViewModels;

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
        var vm = new UnlockViewModel(_closing);
        vm.Unlocked += OnUnlocked;
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
    }
}
