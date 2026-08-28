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

    /// <summary>Live strength readout for the new password — same feedback the create screen
    /// gives, so Change Password can't silently downgrade the vault to a weak password.</summary>
    [ObservableProperty] private string _newPasswordStrength = string.Empty;

    partial void OnNewPasswordChanged(string value)
    {
        var (level, bits) = PasswordStrength.Evaluate(value);
        NewPasswordStrength = value.Length == 0 ? string.Empty : $"{level} (~{bits:0} bits)";
    }

    // Optional SOCKS proxy (e.g. Tor) for the wallet backend's daemon traffic.
    [ObservableProperty] private string _proxyAddress;

    // Vault backup (export / restore the encrypted vault file)
    [ObservableProperty] private string _backupResult = string.Empty;
    [ObservableProperty] private bool _backupOk;

    /// <summary>Set by the View: save-file picker returning the destination path (or null).</summary>
    public Func<string, Task<string?>>? ExportPickHandler { get; set; }

    /// <summary>Set by the View: open-file picker returning the backup to restore (or null).</summary>
    public Func<Task<string?>>? RestorePickHandler { get; set; }

    /// <summary>Restoring a vault out from under an OPEN wallet is forbidden — lock first.
    /// Deliberately NOT conditioned on a vault existing: the primary recovery case is a lost
    /// vault file with only the exported backup in hand.</summary>
    public bool CanRestoreVault { get; }

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

    public SettingsViewModel(bool walletOpen = false)
    {
        AppSettings s = AppServices.Instance.Settings;
        _walletRpcBinaryPath = s.WalletRpcBinaryPath;
        _defaultDaemonAddress = s.DefaultDaemonAddress;
        _networkIndex = s.DefaultNetworkIndex;
        _autoRefreshSeconds = s.AutoRefreshSeconds;
        _autoLockMinutes = s.AutoLockMinutes;
        _proxyAddress = s.ProxyAddress;
        CanRestoreVault = !walletOpen;
        DefaultBinaryHint = "Leave blank to auto-detect. Currently resolves to: " +
                            AppServices.Instance.ResolvedDefaultWalletRpcBinary;

        if (AppSettings.RecoveredFromCorruptFile)
        {
            SavedMessage = "Settings could not be read and were reset to defaults. " +
                           "The unreadable file was kept as settings.json.bad.";
        }
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
            // Probe through the proxy currently typed in this screen, saved or not: the test
            // must exercise the same route the wallet will actually use.
            ulong height = await MoneroDiagnostics.ProbeDaemonAsync(DefaultDaemonAddress, ProxyAddress);
            DaemonTestOk = true;
            DaemonTestResult = $"OK \u2014 node at height {height}.";
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
        // Validate BEFORE persisting: saved garbage surfaces later as a generic
        // "waiting for node" with no hint that Settings is the culprit.
        string daemon = (DefaultDaemonAddress ?? string.Empty).Trim();
        if (daemon.Length > 0 && !DaemonAddress.IsValid(daemon))
        {
            SavedMessage = "Default node must be a valid http(s) URL, e.g. http://127.0.0.1:18081 — not saved.";
            return;
        }

        string proxy = (ProxyAddress ?? string.Empty).Trim();
        if (proxy.Length > 0 && !IsValidProxy(proxy))
        {
            SavedMessage = "Proxy must be host:port (e.g. 127.0.0.1:9050 for Tor) — not saved.";
            return;
        }

        try
        {
            AppSettings s = AppServices.Instance.Settings;
            s.WalletRpcBinaryPath = (WalletRpcBinaryPath ?? string.Empty).Trim();
            s.DefaultDaemonAddress = (DefaultDaemonAddress ?? string.Empty).Trim();
            s.DefaultNetworkIndex = NetworkIndex;
            s.AutoRefreshSeconds = AutoRefreshSeconds;
            s.AutoLockMinutes = AutoLockMinutes;
            s.ProxyAddress = proxy;
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
    private async Task ChangePasswordAsync()
    {
        if (Busy)
        {
            return; // never interleave two vault-mutating operations (lost-update risk)
        }

        ChangePasswordResult = string.Empty;
        ChangePasswordOk = false;

        if (string.IsNullOrEmpty(CurrentPassword) || string.IsNullOrEmpty(NewPassword))
        {
            ChangePasswordResult = "Enter your current and new password.";
            return;
        }

        if (NewPassword.Length < 8)
        {
            // Same floor as vault creation — this path must not quietly downgrade the vault.
            ChangePasswordResult = "New password must be at least 8 characters.";
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

        Busy = true;
        try
        {
            char[] curChars = CurrentPassword.ToCharArray();
            char[] nextChars = NewPassword.ToCharArray();
            CurrentPassword = NewPassword = NewPasswordConfirm = string.Empty;

            // Argon2id at these parameters takes seconds — keep it OFF the UI thread so the
            // window never looks hung (a user who kills a "frozen" app mid-rewrite is the
            // failure mode the atomic vault write exists to survive, not to invite).
            bool changed = await Task.Run(() =>
            {
                // Take ownership of the password chars FIRST (FromPassword zeroes them): if
                // Load throws, the passwords must not be left un-zeroed on the heap.
                using var cur = SecureBuffer.FromPassword(curChars);
                using var next = SecureBuffer.FromPassword(nextChars);
                VaultManager mgr = VaultManager.Load(AppServices.Instance.VaultPath);

                // ChangeMainPassword only succeeds for the REAL slot; the duress password is rejected.
                return mgr.ChangeMainPassword(cur, next);
            });

            if (changed)
            {
                ChangePasswordOk = true;
                ChangePasswordResult = "Password changed.";
                Log.Info("Master password changed.");
            }
            else
            {
                ChangePasswordOk = false;
                ChangePasswordResult = "That current password didn't unlock the real wallet.";
            }
        }
        catch (ArgumentException ex)
        {
            // e.g. the new password is unusable for this vault (kept deliberately neutral).
            ChangePasswordOk = false;
            ChangePasswordResult = ex.Message;
        }
        catch (Exception ex)
        {
            ChangePasswordOk = false;
            ChangePasswordResult = "Couldn't change password: " + ex.Message;
            Log.Error("Change password failed", ex);
        }
        finally
        {
            Busy = false;
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
            ulong height = await MoneroDiagnostics.ProbeDaemonAsync(RepointNodeAddress, ProxyAddress);
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
    private async Task RepointNodeAsync()
    {
        if (Busy)
        {
            return; // never interleave two vault-mutating operations (lost-update risk)
        }

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

        Busy = true;
        try
        {
            char[] pwChars = RepointPassword.ToCharArray();
            RepointPassword = string.Empty;
            string address = RepointNodeAddress.Trim();

            // Argon2id derivation off the UI thread — same reasoning as ChangePassword.
            bool changed = await Task.Run(() =>
            {
                // SecureBuffer first — same heap-hygiene reasoning as ChangePassword.
                using var pw = SecureBuffer.FromPassword(pwChars);
                VaultManager mgr = VaultManager.Load(AppServices.Instance.VaultPath);

                // ChangeDaemonAddress repoints whichever profile the password opens (real or
                // duress), so the wording here stays neutral and never hints at a second wallet.
                return mgr.ChangeDaemonAddress(pw, address);
            });

            if (changed)
            {
                RepointOk = true;
                RepointResult = "Node updated. Lock and unlock your wallet for the change to take effect.";
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
        finally
        {
            Busy = false;
        }
    }

    // ---- Vault backup ----

    /// <summary>Copy the encrypted vault file to a user-chosen location. The copy is exactly as
    /// strong as the vault itself (Argon2id + AES-256-GCM) — but note it also outlives a later
    /// duress wipe, so it must be stored somewhere a coercer can't find.</summary>
    [RelayCommand]
    private async Task ExportVaultAsync()
    {
        BackupResult = string.Empty;
        BackupOk = false;

        if (!VaultExists || ExportPickHandler is null)
        {
            BackupResult = "There is no vault to export.";
            return;
        }

        try
        {
            string? dest = await ExportPickHandler($"xaultwallet-vault-backup-{DateTime.Now:yyyyMMdd}.xv");
            if (string.IsNullOrWhiteSpace(dest))
            {
                return; // cancelled
            }

            byte[] bytes = File.ReadAllBytes(AppServices.Instance.VaultPath);
            VaultFile.Deserialize(bytes); // sanity: never export a corrupt vault as a "backup"
            File.WriteAllBytes(dest, bytes);
            BackupOk = true;
            BackupResult = "Encrypted vault backup saved. Store it somewhere safe — it stays " +
                           "protected by your password, but anyone holding it can try to brute-force it.";
            // Deliberately NOT logged: a log line proving a backup exists outlives a later
            // duress wipe and hands a coercer exactly the lead the wipe is meant to erase.
        }
        catch (Exception ex)
        {
            BackupResult = "Couldn't export the backup: " + ex.Message;
        }
    }

    /// <summary>Replace the live vault with a previously exported backup. Only offered when no
    /// wallet is unlocked. The replaced vault is KEPT next to the original (timestamped), never
    /// destroyed — a bad restore must always be reversible.</summary>
    [RelayCommand]
    private async Task RestoreVaultAsync()
    {
        if (Busy)
        {
            return; // never interleave with a vault-mutating operation
        }

        BackupResult = string.Empty;
        BackupOk = false;

        if (RestorePickHandler is null)
        {
            return;
        }

        Busy = true;
        try
        {
            await RestoreVaultCoreAsync();
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RestoreVaultCoreAsync()
    {

        if (!CanRestoreVault)
        {
            BackupResult = "Lock the wallet before restoring a backup.";
            return;
        }

        string vaultPath = AppServices.Instance.VaultPath;
        string? kept = null;
        string tmp = vaultPath + ".tmp";
        try
        {
            string? source = RestorePickHandler is { } pick ? await pick() : null;
            if (string.IsNullOrWhiteSpace(source))
            {
                return; // cancelled (or no picker wired)
            }

            byte[] bytes = File.ReadAllBytes(source);
            VaultFile.Deserialize(bytes); // validate BEFORE touching the live vault

            // Durable write of the replacement FIRST (flushed to disk), then a single atomic
            // swap — matching VaultManager.Persist's discipline. The live vault path holds
            // either the old vault or the new one at every instant, even across a crash or
            // power loss; there is never a window with NO vault at the live path.
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(vaultPath))
            {
                kept = vaultPath + $".replaced-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Replace(tmp, vaultPath, kept);
            }
            else
            {
                File.Move(tmp, vaultPath);
            }

            BackupOk = true;
            BackupResult = kept is null
                ? "Backup restored. Close Settings to unlock it."
                : $"Backup restored. The previous vault was kept as {Path.GetFileName(kept)}.";
            // Not logged — same deniability reasoning as export.
        }
        catch (InvalidDataException)
        {
            BackupResult = "That file is not a valid XaultWallet vault backup.";
        }
        catch (Exception ex)
        {
            // A failed restore must never leave the user with NO vault: put the original back.
            string recovery = TryRollbackRestore(vaultPath, kept, tmp);
            BackupResult = "Couldn't restore the backup: " + ex.Message + recovery;
        }
    }

    /// <summary>Best-effort rollback after a failed restore; returns a note for the UI.</summary>
    private static string TryRollbackRestore(string vaultPath, string? kept, string tmp)
    {
        try { if (File.Exists(tmp)) { File.Delete(tmp); } } catch { /* best effort */ }

        if (kept is null || File.Exists(vaultPath))
        {
            return string.Empty; // nothing was moved aside, or the live vault survived
        }

        try
        {
            File.Move(kept, vaultPath);
            return " Your original vault is untouched.";
        }
        catch
        {
            return $" IMPORTANT: your original vault is preserved as {Path.GetFileName(kept)} " +
                   "in the data folder — rename it back to vault.xv to recover.";
        }
    }

    // ---- Folder shortcuts ----

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppServices.Instance.DataDirectory);

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(AppServices.Instance.LogsDirectory);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SavedMessage = "Couldn't open the folder: " + ex.Message;
        }
    }

    /// <summary>host:port with a sane port — the only shape monero-wallet-rpc's --proxy accepts.</summary>
    private static bool IsValidProxy(string proxy)
    {
        int colon = proxy.LastIndexOf(':');
        return colon > 0
               && colon < proxy.Length - 1
               && !proxy.Contains("://", StringComparison.Ordinal)
               && int.TryParse(proxy[(colon + 1)..], out int port)
               && port is >= 1 and <= 65535;
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
