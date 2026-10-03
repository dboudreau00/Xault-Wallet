using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private WalletViewModel? _wallet;
    private ViewModelBase? _beforeSettings;

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
        var vm = new UnlockViewModel();
        vm.Unlocked += OnUnlocked;
        return vm;
    }

    /// <summary>Forwarded from the window on any input; defers auto-lock while a wallet is open.</summary>
    public void NotifyActivity() => _wallet?.NotifyActivity();

    private CreateWalletViewModel BuildCreate()
    {
        var vm = new CreateWalletViewModel();
        vm.Created += () => Current = BuildUnlock();
        return vm;
    }

    private void OnUnlocked(WalletSecrets secrets)
    {
        // The UI is identical whichever wallet was opened — by construction: the vault returns the
        // same kind of secrets for both slots and nothing downstream could tell them apart.
        _wallet = new WalletViewModel(secrets);
        _wallet.Locked += () =>
        {
            // Locked can fire twice when a manual Lock click races the inactivity auto-lock
            // (both call LockAsync; DisposeAsync is idempotent but each raises the event).
            WalletViewModel? w = _wallet;
            if (w is null)
            {
                return;
            }

            _ = w.DisposeAsync();
            _wallet = null;

            // If the auto-lock fires while Settings is open, close the Settings state too —
            // otherwise its Closed handler later re-navigates over whatever screen is showing
            // (and the gear button stays hidden on the unlock screen).
            SettingsOpen = false;
            _beforeSettings = null;

            Current = BuildUnlock();
        };
        // Let the wallet screen (e.g. its startup-failure banner) open Settings through the shell.
        _wallet.SettingsRequested += OpenSettings;
        Current = _wallet;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (SettingsOpen)
        {
            return;
        }

        _beforeSettings = Current;
        var settings = new SettingsViewModel(walletOpen: _wallet is not null);
        settings.Closed += () =>
        {
            SettingsOpen = false;

            // If a wallet is open, return to it unchanged. Otherwise rebuild the create/unlock
            // screen fresh so it picks up any node/network default just changed in Settings
            // (the pre-settings instance would still hold the old default).
            if (_wallet is not null && ReferenceEquals(_beforeSettings, _wallet))
            {
                Current = _wallet;
            }
            else if (VaultManager.Exists(AppServices.Instance.VaultPath))
            {
                Current = BuildUnlock();
            }
            else
            {
                Current = BuildCreate();
            }
            _beforeSettings = null;
        };

        SettingsOpen = true;
        Current = settings;
    }

    public async Task ShutdownAsync()
    {
        if (_wallet is not null)
        {
            await _wallet.DisposeAsync();
        }
    }
}
