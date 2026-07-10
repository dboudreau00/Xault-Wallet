using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop.ViewModels;

public sealed partial class UnlockViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private bool _busy;

    /// <summary>Raised on a correct password. The bool indicates duress, but callers must not display it.</summary>
    public event Action<WalletSecrets, bool>? Unlocked;

    [RelayCommand]
    private async Task UnlockAsync()
    {
        Error = string.Empty;

        if (string.IsNullOrEmpty(Password))
        {
            Error = "Enter your password.";
            return;
        }

        Busy = true;
        try
        {
            char[] chars = Password.ToCharArray();
            Password = string.Empty; // clear the bound field ASAP

            UnlockResult? result = await Task.Run(() =>
            {
                var mgr = VaultManager.Load(AppServices.Instance.VaultPath);
                using var pw = SecureBuffer.FromPassword(chars);
                return mgr.Unlock(pw);
            });

            if (result is null)
            {
                // Deliberately generic. Never hint that a duress password exists.
                Error = "Incorrect password.";
                return;
            }

            Unlocked?.Invoke(result.Secrets, result.WasDuress);
        }
        catch (Exception ex)
        {
            XaultWallet.Core.Diagnostics.Log.Error("Unlock failed", ex);
            Error = ex is InvalidDataException or FileNotFoundException or IOException
                ? ex.Message
                : "Could not open the vault.";
        }
        finally
        {
            Busy = false;
        }
    }
}
