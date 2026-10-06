using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>The "Manage wallet" sheet: rename, node, backup (seed and keys), accounts, remove.</summary>
public sealed partial class WalletViewModel
{
    [ObservableProperty] private bool _showManage;

    /// <summary>Feedback inside the sheet.</summary>
    [ObservableProperty] private string _manageNotice = string.Empty;
    [ObservableProperty] private bool _manageNoticeIsError;

    [RelayCommand]
    private void OpenManage()
    {
        ManageNotice = string.Empty;
        RenameText = _secrets.Name;
        NodeAddress = _secrets.DaemonAddress;
        AccountRenameText = SelectedAccount?.Label ?? string.Empty;
        ShowManage = true;
    }

    [RelayCommand]
    private void CloseManage()
    {
        ShowManage = false;
        WipeRevealedSecrets();
        RevealPassword = RemovePassword = RemoveConfirmName = string.Empty;
    }

    partial void OnShowManageChanged(bool value)
    {
        if (!value)
        {
            WipeRevealedSecrets();
        }
    }

    private void Notice(string text, bool error = false)
    {
        ManageNotice = text;
        ManageNoticeIsError = error;
    }

    // ------------------------------------------------------------------ name

    [ObservableProperty] private string _renameText = string.Empty;

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (await _profile.RenameAsync(WalletId, RenameText) is { } error)
        {
            Notice(error, error: true);
            return;
        }

        Notice("Renamed.");
    }

    // ------------------------------------------------------------------ node

    [ObservableProperty] private string _nodeAddress = string.Empty;

    [RelayCommand]
    private async Task TestNodeAsync()
    {
        Notice("Contacting the node…");
        try
        {
            ulong height = await MoneroDiagnostics.ProbeDaemonAsync(NodeAddress.Trim(), AppServices.Instance.Settings.ProxyAddress, _cts.Token);
            Notice($"OK — node at height {height:N0}.");
        }
        catch (Exception ex)
        {
            Notice(ex.Message, error: true);
        }
    }

    /// <summary>Use another node for this wallet: switched live (no rescan) and saved for next time.</summary>
    [RelayCommand]
    private async Task ApplyNodeAsync()
    {
        string address = NodeAddress.Trim();
        if (!DaemonAddress.IsValid(address))
        {
            Notice("The node must be an http(s) URL, e.g. http://127.0.0.1:18081.", error: true);
            return;
        }

        if (address == _secrets.DaemonAddress)
        {
            Notice("That's already this wallet's node.");
            return;
        }

        string before = _secrets.DaemonAddress;
        try
        {
            if (IsReady)
            {
                await _wallet.SetDaemonAsync(address, _cts.Token);
            }

            _secrets.DaemonAddress = address;
            if (await _profile.SaveAsync() is { } error)
            {
                _secrets.DaemonAddress = before;
                if (IsReady)
                {
                    await _wallet.SetDaemonAsync(before, _cts.Token);
                }

                Notice(error, error: true);
                return;
            }

            Notice("This wallet now uses that node.");
            OnPropertyChanged(nameof(CanRescanSpent));
            _nextRefreshUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Notice("Couldn't switch to that node: " + Friendly(ex), error: true);
        }
    }

    // ------------------------------------------------------------------ backup: seed and keys

    [ObservableProperty] private string _revealPassword = string.Empty;
    [ObservableProperty] private bool _secretsShown;
    [ObservableProperty] private string _revealedMnemonic = string.Empty;
    [ObservableProperty] private string _revealedSeedOffset = string.Empty;
    [ObservableProperty] private string _revealedViewKey = string.Empty;
    [ObservableProperty] private string _revealedSpendKey = string.Empty;

    public bool HasMnemonic => RevealedMnemonic.Length > 0;

    /// <summary>Show this wallet's seed and private keys, after the vault password (someone at an
    /// unlocked computer shouldn't be able to read them off the screen).</summary>
    [RelayCommand]
    private async Task RevealSecretsAsync()
    {
        if (RevealPassword.Length == 0)
        {
            Notice("Enter your password to show the seed and keys.", error: true);
            return;
        }

        char[] password = RevealPassword.ToCharArray();
        RevealPassword = string.Empty;
        if (!await _profile.CheckPasswordAsync(password))
        {
            Notice("Incorrect password.", error: true);
            return;
        }

        try
        {
            (string mnemonic, string viewKey, string spendKey) = IsReady
                ? await _wallet.GetKeysAsync(_cts.Token)
                : (_secrets.Mnemonic, _secrets.ViewKey, _secrets.SpendKey);
            RevealedMnemonic = _secrets.Kind == WalletKind.Seed ? _secrets.Mnemonic : mnemonic.Trim();
            RevealedSeedOffset = _secrets.SeedOffset;
            RevealedViewKey = viewKey.Length > 0 ? viewKey : _secrets.ViewKey;
            RevealedSpendKey = _secrets.Kind == WalletKind.ViewOnly || spendKey.Trim('0').Length == 0 ? string.Empty : spendKey;
            OnPropertyChanged(nameof(HasMnemonic));
            SecretsShown = true;
            Notice("Anyone who sees the seed or the spend key can take everything in this wallet. Hide them when you're done.", error: true);
        }
        catch (Exception ex)
        {
            Notice("Couldn't read the keys: " + Friendly(ex), error: true);
        }
    }

    [RelayCommand]
    private void HideSecrets()
    {
        WipeRevealedSecrets();
        ManageNotice = string.Empty;
    }

    private void WipeRevealedSecrets()
    {
        SecretsShown = false;
        RevealedMnemonic = RevealedSeedOffset = RevealedViewKey = RevealedSpendKey = string.Empty;
        OnPropertyChanged(nameof(HasMnemonic));
    }

    [RelayCommand]
    private void CopyViewKey() => Copy(RevealedViewKey, "View key");

    // ------------------------------------------------------------------ accounts

    [ObservableProperty] private string _newAccountLabel = string.Empty;
    [ObservableProperty] private string _accountRenameText = string.Empty;

    partial void OnSelectedAccountChanging(AccountChoice? value) => AccountRenameText = value?.Label ?? string.Empty;

    /// <summary>A new account: a separate balance inside the same wallet (same seed).</summary>
    [RelayCommand]
    private async Task CreateAccountAsync()
    {
        if (!IsReady)
        {
            return;
        }

        string label = NewAccountLabel.Trim();
        if (label.Length > MaxLabelLength)
        {
            Notice($"Keep the name under {MaxLabelLength} characters.", error: true);
            return;
        }

        try
        {
            (uint index, _) = await _wallet.NewAccountAsync(_cts.Token);
            _secrets.SubaddressCounts[index] = Math.Max(_secrets.SubaddressCounts.GetValueOrDefault(index), 1);
            if (label.Length > 0)
            {
                _secrets.AccountLabels[index] = label;
            }

            if (await _profile.SaveAsync() is { } error)
            {
                Notice("The account was created, but couldn't be recorded in the vault: " + error, error: true);
            }
            else
            {
                Notice("Account created.");
            }

            NewAccountLabel = string.Empty;
            await RefreshAccountsAsync(_cts.Token);
            SelectedAccount = Accounts.FirstOrDefault(a => a.Index == index) ?? SelectedAccount;
        }
        catch (Exception ex)
        {
            Notice("Couldn't create an account: " + Friendly(ex), error: true);
        }
    }

    [RelayCommand]
    private async Task RenameAccountAsync()
    {
        if (SelectedAccount is not { } account)
        {
            return;
        }

        string label = AccountRenameText.Trim();
        if (label.Length > MaxLabelLength)
        {
            Notice($"Keep the name under {MaxLabelLength} characters.", error: true);
            return;
        }

        string? before = _secrets.AccountLabels.GetValueOrDefault(account.Index);
        if (label.Length == 0)
        {
            _secrets.AccountLabels.Remove(account.Index);
        }
        else
        {
            _secrets.AccountLabels[account.Index] = label;
        }

        if (await _profile.SaveAsync() is { } error)
        {
            if (before is null)
            {
                _secrets.AccountLabels.Remove(account.Index);
            }
            else
            {
                _secrets.AccountLabels[account.Index] = before;
            }

            Notice(error, error: true);
            return;
        }

        account.Label = label;
        Notice("Account renamed.");
    }

    // ------------------------------------------------------------------ remove

    [ObservableProperty] private string _removePassword = string.Empty;
    [ObservableProperty] private string _removeConfirmName = string.Empty;

    /// <summary>Remove this wallet from the vault. Needs the password AND its name typed out: the seed
    /// is gone for good unless it's written down somewhere.</summary>
    [RelayCommand]
    private async Task RemoveWalletAsync()
    {
        if (!string.Equals(RemoveConfirmName.Trim(), _secrets.Name, StringComparison.Ordinal))
        {
            Notice($"Type the wallet's name (“{_secrets.Name}”) to confirm.", error: true);
            return;
        }

        if (RemovePassword.Length == 0)
        {
            Notice("Enter your password to remove this wallet.", error: true);
            return;
        }

        char[] password = RemovePassword.ToCharArray();
        RemovePassword = string.Empty;
        if (await _profile.RemoveAsync(WalletId, password) is { } error)
        {
            Notice(error, error: true);
            return;
        }

        ShowManage = false;
    }
}
