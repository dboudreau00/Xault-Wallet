using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>Show the typed password (long passphrases are easy to mistype blind).</summary>
    [ObservableProperty]
    private bool _revealPassword;

    [RelayCommand]
    private void ToggleReveal() => RevealPassword = !RevealPassword;

    /// <summary>Raised on a correct password with the opened profile. Carries no real/decoy signal —
    /// the vault has none.</summary>
    public event Action<VaultSession>? Unlocked;

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

            VaultSession? session = await Task.Run(() =>
            {
                // Take ownership of the password chars FIRST (FromPassword zeroes them): if
                // Load throws (missing/corrupt vault), the full password must not be left
                // un-zeroed on the heap.
                using var pw = SecureBuffer.FromPassword(chars);
                var mgr = VaultManager.Load(AppServices.Instance.VaultPath);
                return mgr.OpenSession(pw);
            });

            if (session is null)
            {
                // Deliberately generic. Never hint that a duress password exists.
                Error = "Incorrect password.";
                return;
            }

            Unlocked?.Invoke(session);
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
