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

    public MainWindowViewModel()
    {
        // First run → create-wallet flow; otherwise → unlock.
        if (VaultManager.Exists(AppServices.Instance.VaultPath))
        {
            _current = BuildUnlock();
        }
        else
        {
            _current = BuildCreate();
        }
    }

    private UnlockViewModel BuildUnlock()
    {
        var vm = new UnlockViewModel();
        vm.Unlocked += OnUnlocked;
        return vm;
    }

    private CreateWalletViewModel BuildCreate()
    {
        var vm = new CreateWalletViewModel();
        vm.Created += () => Current = BuildUnlock();
        return vm;
    }

    private void OnUnlocked(WalletSecrets secrets, bool wasDuress)
    {
        // IMPORTANT: the UI is intentionally identical whether the real or the
        // duress wallet was opened. We do NOT surface `wasDuress` anywhere the
        // user (or a coercer looking over their shoulder) could see it.
        _wallet = new WalletViewModel(secrets);
        _wallet.Locked += () =>
        {
            _ = _wallet.DisposeAsync();
            _wallet = null;
            Current = BuildUnlock();
        };
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
        var settings = new SettingsViewModel();
        settings.Closed += () =>
        {
            SettingsOpen = false;
            if (_beforeSettings is not null)
            {
                Current = _beforeSettings; // restore the exact screen (and its state) we left
                _beforeSettings = null;
            }
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
