using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private string _walletRpcBinaryPath;
    [ObservableProperty] private string _defaultDaemonAddress;
    [ObservableProperty] private int _networkIndex;
    [ObservableProperty] private int _autoRefreshSeconds;
    [ObservableProperty] private int _autoLockMinutes;

    [ObservableProperty] private string _binaryTestResult = string.Empty;
    [ObservableProperty] private bool _binaryTestOk;
    [ObservableProperty] private string _daemonTestResult = string.Empty;
    [ObservableProperty] private bool _daemonTestOk;
    [ObservableProperty] private string _savedMessage = string.Empty;
    [ObservableProperty] private bool _busy;

    // Change master password
    [ObservableProperty] private string _currentPassword = string.Empty;
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _newPasswordConfirm = string.Empty;
    [ObservableProperty] private string _changePasswordResult = string.Empty;
    [ObservableProperty] private bool _changePasswordOk;

    // Change THIS wallet's node (repoint an existing vault's daemon address)
    [ObservableProperty] private string _repointNodeAddress = string.Empty;
    [ObservableProperty] private string _repointPassword = string.Empty;
    [ObservableProperty] private string _repointResult = string.Empty;
    [ObservableProperty] private bool _repointOk;
    [ObservableProperty] private string _repointTestResult = string.Empty;
    [ObservableProperty] private bool _repointTestOk;

    /// <summary>Selecting a preset fills the repoint address field below (network is unchanged).</summary>
    [ObservableProperty] private RemoteNode? _selectedRepointPreset;

    /// <summary>Only show the repoint card when there's actually a wallet to repoint.</summary>
    public bool VaultExists { get; } = VaultManager.Exists(AppServices.Instance.VaultPath);

    partial void OnSelectedRepointPresetChanged(RemoteNode? value)
    {
        if (value is null)
        {
            return;
        }

        RepointNodeAddress = value.Url;
        RepointResult = string.Empty;
        RepointTestResult = string.Empty;
    }

    partial void OnRepointNodeAddressChanged(string value)
    {
        RepointResult = string.Empty;
        RepointTestResult = string.Empty;
    }

    /// <summary>Selecting a preset fills the daemon address and network below.</summary>
    [ObservableProperty] private RemoteNode? _selectedPreset;

    public IReadOnlyList<RemoteNode> PresetNodes => RemoteNodes.All;

    partial void OnSelectedPresetChanged(RemoteNode? value)
    {
        if (value is null)
        {
            return;
        }

        DefaultDaemonAddress = value.Url;
        NetworkIndex = value.NetworkIndex;
        SavedMessage = string.Empty;
        DaemonTestResult = string.Empty;
    }

    /// <summary>Set by the View: opens a file picker and returns the chosen path (or null).</summary>
    public Func<Task<string?>>? BrowseHandler { get; set; }

    public event Action? Closed;

    public string DefaultBinaryHint { get; }

    public SettingsViewModel()
    {
        AppSettings s = AppServices.Instance.Settings;
        _walletRpcBinaryPath = s.WalletRpcBinaryPath;
        _defaultDaemonAddress = s.DefaultDaemonAddress;
        _networkIndex = s.DefaultNetworkIndex;
        _autoRefreshSeconds = s.AutoRefreshSeconds;
        _autoLockMinutes = s.AutoLockMinutes;
        DefaultBinaryHint = "Leave blank to auto-detect. Currently resolves to: " +
                            AppServices.Instance.ResolvedDefaultWalletRpcBinary;
    }

    partial void OnWalletRpcBinaryPathChanged(string value) => SavedMessage = string.Empty;
    partial void OnDefaultDaemonAddressChanged(string value) => SavedMessage = string.Empty;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (BrowseHandler is null)
        {
            return;
        }

        string? picked = await BrowseHandler();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            WalletRpcBinaryPath = picked;
        }
    }

    [RelayCommand]
    private async Task TestBinaryAsync()
    {
        Busy = true;
        BinaryTestOk = false;
        BinaryTestResult = "Testing\u2026";
        try
        {
            string path = string.IsNullOrWhiteSpace(WalletRpcBinaryPath)
                ? AppServices.Instance.ResolvedDefaultWalletRpcBinary
                : WalletRpcBinaryPath.Trim();

            string version = await MoneroDiagnostics.ProbeWalletRpcAsync(path);
            BinaryTestOk = true;
            BinaryTestResult = "OK \u2014 " + version;
        }
        catch (Exception ex)
        {
            BinaryTestOk = false;
            BinaryTestResult = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task TestDaemonAsync()
    {
        Busy = true;
        DaemonTestOk = false;
        DaemonTestResult = "Contacting daemon\u2026";
        try
        {
            ulong height = await MoneroDiagnostics.ProbeDaemonAsync(DefaultDaemonAddress);
            DaemonTestOk = true;
            DaemonTestResult = $"OK \u2014 daemon at height {height}.";
        }
        catch (Exception ex)
        {
            DaemonTestOk = false;
            DaemonTestResult = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            AppSettings s = AppServices.Instance.Settings;
            s.WalletRpcBinaryPath = (WalletRpcBinaryPath ?? string.Empty).Trim();
            s.DefaultDaemonAddress = (DefaultDaemonAddress ?? string.Empty).Trim();
            s.DefaultNetworkIndex = NetworkIndex;
            s.AutoRefreshSeconds = AutoRefreshSeconds;
            s.AutoLockMinutes = AutoLockMinutes;
            AppServices.Instance.SaveSettings();

            // Reflect any clamping back into the fields.
            AutoRefreshSeconds = s.AutoRefreshSeconds;
            AutoLockMinutes = s.AutoLockMinutes;
            SavedMessage = "Settings saved.";
            Log.Info("Settings saved.");
        }
        catch (Exception ex)
        {
            SavedMessage = "Couldn't save settings: " + ex.Message;
            Log.Error("Saving settings failed", ex);
        }
    }

    [RelayCommand]
    private void ChangePassword()
    {
        ChangePasswordResult = string.Empty;
        ChangePasswordOk = false;

        if (string.IsNullOrEmpty(CurrentPassword) || string.IsNullOrEmpty(NewPassword))
        {
            ChangePasswordResult = "Enter your current and new password.";
            return;
        }

        if (NewPassword != NewPasswordConfirm)
        {
            ChangePasswordResult = "New passwords don't match.";
            return;
        }

        if (NewPassword == CurrentPassword)
        {
            ChangePasswordResult = "New password must differ from the current one.";
            return;
        }

        try
        {
            VaultManager mgr = VaultManager.Load(AppServices.Instance.VaultPath);
            using var cur = SecureBuffer.FromPassword(CurrentPassword.ToCharArray());
            using var next = SecureBuffer.FromPassword(NewPassword.ToCharArray());

            // ChangeMainPassword only succeeds for the REAL slot; the duress password is rejected.
            if (mgr.ChangeMainPassword(cur, next))
            {
                ChangePasswordOk = true;
                ChangePasswordResult = "Password changed.";
                CurrentPassword = NewPassword = NewPasswordConfirm = string.Empty;
                Log.Info("Master password changed.");
            }
            else
            {
                ChangePasswordOk = false;
                ChangePasswordResult = "That current password didn't unlock the real wallet.";
            }
        }
        catch (Exception ex)
        {
            ChangePasswordOk = false;
            ChangePasswordResult = "Couldn't change password: " + ex.Message;
            Log.Error("Change password failed", ex);
        }
    }

    [RelayCommand]
    private async Task TestRepointNodeAsync()
    {
        Busy = true;
        RepointTestOk = false;
        RepointTestResult = "Contacting node…";
        try
        {
            ulong height = await MoneroDiagnostics.ProbeDaemonAsync(RepointNodeAddress);
            RepointTestOk = true;
            RepointTestResult = $"OK — node at height {height}.";
        }
        catch (Exception ex)
        {
            RepointTestOk = false;
            RepointTestResult = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void RepointNode()
    {
        RepointResult = string.Empty;
        RepointOk = false;

        if (string.IsNullOrWhiteSpace(RepointNodeAddress))
        {
            RepointResult = "Enter the new node address.";
            return;
        }

        if (string.IsNullOrEmpty(RepointPassword))
        {
            RepointResult = "Enter your wallet password to confirm the change.";
            return;
        }

        try
        {
            VaultManager mgr = VaultManager.Load(AppServices.Instance.VaultPath);
            using var pw = SecureBuffer.FromPassword(RepointPassword.ToCharArray());

            // ChangeDaemonAddress repoints whichever profile the password opens (real or duress),
            // so the wording here stays neutral and never hints at a second wallet.
            if (mgr.ChangeDaemonAddress(pw, RepointNodeAddress.Trim()))
            {
                RepointOk = true;
                RepointResult = "Node updated. Lock and unlock your wallet for the change to take effect.";
                RepointPassword = string.Empty;
                // Deliberately not logging the address — which node you use is not something the log needs.
                Log.Info("Wallet daemon address repointed.");
            }
            else
            {
                RepointOk = false;
                RepointResult = "That password didn't unlock a wallet in this vault.";
            }
        }
        catch (ArgumentException ex)
        {
            RepointOk = false;
            RepointResult = ex.Message;
        }
        catch (Exception ex)
        {
            RepointOk = false;
            RepointResult = "Couldn't update the node: " + ex.Message;
            Log.Error("Repoint node failed", ex);
        }
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
