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

    // Change password (symmetric: works for whichever wallet the current password opens)
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

    // ---- How the app reaches the network: 1 built-in Tor, 2 the user's own SOCKS proxy, 0 direct ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TorSelected))]
    [NotifyPropertyChangedFor(nameof(ProxySelected))]
    private int _networkMode;

    public bool TorSelected => NetworkMode == 1;

    public bool ProxySelected => NetworkMode == 2;

    partial void OnNetworkModeChanged(int value)
    {
        SavedMessage = string.Empty;
        DaemonTestResult = string.Empty;
    }

    /// <summary>The app's own Tor: its live status, start and stop.</summary>
    public TorController Tor => AppServices.Instance.Tor;

    /// <summary>"Download &amp; install Tor".</summary>
    public TorSetupViewModel TorSetup { get; } = new();

    /// <summary>Explicit tor binary (blank = the installed one, else tor on PATH).</summary>
    [ObservableProperty] private string _torBinaryPath;

    [ObservableProperty] private string _torBinaryHint = string.Empty;

    [ObservableProperty] private string _torTestResult = string.Empty;

    [ObservableProperty] private bool _torTestOk;

    /// <summary>Set by the View: file picker for the tor binary.</summary>
    public Func<Task<string?>>? BrowseTorHandler { get; set; }

    private void RefreshTorHint()
    {
        string detected = TorInstalledOrOnPath();
        TorBinaryHint = detected.Length > 0
            ? "Leave blank to use: " + detected
            : "Not installed yet: Download & install Tor below, or enter the full path to your own tor.";
    }

    private static string TorInstalledOrOnPath() =>
        XaultWallet.Core.Tor.TorInstaller.FindInstalled(AppServices.Instance.TorInstallRoot)
        ?? ExecutableLocator.FindOnPath(XaultWallet.Core.Tor.TorInstaller.TorFileName, Environment.GetEnvironmentVariable("PATH"))
        ?? string.Empty;

    [RelayCommand]
    private async Task BrowseTorAsync()
    {
        if (BrowseTorHandler is not null && await BrowseTorHandler() is { } picked && !string.IsNullOrWhiteSpace(picked))
        {
            TorBinaryPath = picked;
        }
    }

    /// <summary>Run the tor that would be used ("--version"), without starting it.</summary>
    [RelayCommand]
    private async Task TestTorAsync()
    {
        TorTestOk = false;
        TorTestResult = "Testing…";
        try
        {
            string path = string.IsNullOrWhiteSpace(TorBinaryPath)
                ? TorInstalledOrOnPath()
                : ExecutableLocator.ResolveConfigured(TorBinaryPath, Environment.GetEnvironmentVariable("PATH"));
            if (path.Length == 0)
            {
                TorTestResult = "No tor found. Download & install it below, or enter the full path to your own.";
                return;
            }

            string version = await XaultWallet.Core.Tor.TorDiagnostics.ProbeTorAsync(path);
            TorTestOk = true;
            TorTestResult = $"OK: {version.TrimEnd('.')} ({path})";
        }
        catch (Exception ex)
        {
            TorTestResult = ex.Message;
        }
    }

    /// <summary>Start Tor now (or restart it), with what is saved.</summary>
    [RelayCommand]
    private async Task RestartTorAsync()
    {
        try
        {
            await Tor.RestartAsync();
        }
        catch (TorNotReadyException)
        {
            // Tor.StatusText already says why.
        }
    }

    [RelayCommand]
    private Task StopTorAsync() => Tor.StopAsync();

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

    /// <summary>The open profile, when Settings was opened from an unlocked wallet. Password changes
    /// then go through its session (and only ever change its own password).</summary>
    private readonly ProfileViewModel? _profile;

    /// <summary>The password card and the export button need a vault to act on.</summary>
    public bool VaultExists { get; } = VaultManager.Exists(AppServices.Instance.VaultPath);

    // ---- Vault format (a vault from 0.1–0.3, upgraded only on request) ----

    /// <summary>Shown while a wallet is open in a vault that still has the old format (the upgrade
    /// re-seals the open profile). Stays up after upgrading, to show the result.</summary>
    public bool ShowFormatCard { get; }

    /// <summary>The vault still has the old format: the upgrade button is offered.</summary>
    public bool VaultIsOldFormat => _profile?.VaultIsOldFormat == true;

    /// <summary>Second step: the warning is read, the button now upgrades.</summary>
    [ObservableProperty] private bool _confirmFormatUpgrade;
    [ObservableProperty] private string _formatUpgradeResult = string.Empty;
    [ObservableProperty] private bool _formatUpgradeOk;

    [RelayCommand]
    private void AskFormatUpgrade()
    {
        FormatUpgradeResult = string.Empty;
        ConfirmFormatUpgrade = true;
    }

    [RelayCommand]
    private void CancelFormatUpgrade() => ConfirmFormatUpgrade = false;

    [RelayCommand]
    private async Task UpgradeFormatAsync()
    {
        if (_profile is null || Busy)
        {
            return;
        }

        ConfirmFormatUpgrade = false;
        Busy = true;
        try
        {
            string? error = await _profile.UpgradeVaultFormatAsync();
            FormatUpgradeOk = error is null;
            FormatUpgradeResult = error ?? "Upgraded. The wallets, contacts and notes of this password now have room to grow.";
            OnPropertyChanged(nameof(VaultIsOldFormat));
        }
        finally
        {
            Busy = false;
        }
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

    [ObservableProperty] private string _defaultBinaryHint = string.Empty;

    /// <summary>"Download &amp; install" for monero-wallet-rpc.</summary>
    public WalletRpcSetupViewModel Setup { get; } = new();

    /// <summary>"0.5.0-beta" — the informational version without build metadata.</summary>
    public string AppVersion { get; } =
        (System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(SettingsViewModel).Assembly)
            ?.InformationalVersion ?? "").Split('+')[0];

    /// <summary>What remains of the last lock (its final save may still be writing the vault).</summary>
    private readonly Task _closing;

    public SettingsViewModel(ProfileViewModel? profile = null, Task? closing = null)
    {
        _profile = profile;
        _closing = closing ?? Task.CompletedTask;
        AppSettings s = AppServices.Instance.Settings;
        _walletRpcBinaryPath = s.WalletRpcBinaryPath;
        _defaultDaemonAddress = s.DefaultDaemonAddress;
        _networkIndex = s.DefaultNetworkIndex;
        _autoRefreshSeconds = s.AutoRefreshSeconds;
        _autoLockMinutes = s.AutoLockMinutes;
        _proxyAddress = s.ProxyAddress;
        _networkMode = s.UseBuiltInTor ? 1 : s.ProxyAddress.Trim().Length > 0 ? 2 : 0;
        _torBinaryPath = s.TorBinaryPath;
        RefreshTorHint();
        TorSetup.Installed += installed =>
        {
            TorBinaryPath = string.Empty; // the newest install is used when the path is blank
            RefreshTorHint();
            TorTestOk = true;
            TorTestResult = $"OK: {installed.VersionLine.TrimEnd('.')} ({installed.Path})";
            if (AppServices.Instance.Settings.UseBuiltInTor && string.IsNullOrWhiteSpace(AppServices.Instance.Settings.TorBinaryPath))
            {
                _ = RestartTorCommand.ExecuteAsync(null); // onto the new tor
            }
        };
        CanRestoreVault = profile is null;
        ShowFormatCard = profile?.VaultIsOldFormat == true;
        RefreshBinaryHint();

        // A verified install becomes the configured path (the setup view model saved it): show it
        // here at once, with the same result a Test would give.
        Setup.Installed += installed =>
        {
            WalletRpcBinaryPath = installed.Path;
            RefreshBinaryHint();
            BinaryTestOk = true;
            BinaryTestResult = $"OK \u2014 {installed.VersionLine} ({installed.Path})";
        };

        if (AppSettings.RecoveredFromCorruptFile)
        {
            SavedMessage = "Settings could not be read and were reset to defaults. " +
                           "The unreadable file was kept as settings.json.bad.";
        }
    }

    private void RefreshBinaryHint()
    {
        string detected = AppServices.Instance.ResolvedDefaultWalletRpcBinary;
        DefaultBinaryHint = detected.Length > 0
            ? "Leave blank to auto-detect. Currently resolves to: " + detected
            : "Leave blank to auto-detect (nothing found next to the app, on PATH, or installed by XaultWallet yet), or enter the full path.";
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
            // Exactly what a launch would use: blank = auto-detect, bare name = PATH lookup.
            string path = string.IsNullOrWhiteSpace(WalletRpcBinaryPath)
                ? AppServices.Instance.ResolvedDefaultWalletRpcBinary
                : ExecutableLocator.ResolveConfigured(WalletRpcBinaryPath, Environment.GetEnvironmentVariable("PATH"));

            string version = await MoneroDiagnostics.ProbeWalletRpcAsync(path);
            BinaryTestOk = true;
            BinaryTestResult = $"OK \u2014 {version} ({path})";
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
            // Probe the way this screen is set, saved or not: the test must exercise the same
            // route the wallet will actually use.
            string? proxy = NetworkMode switch
            {
                1 when !DaemonAddress.IsLoopback(DefaultDaemonAddress) => await Tor.EnsureStartedAsync(),
                2 => ProxyAddress,
                _ => null,
            };
            ulong height = await MoneroDiagnostics.ProbeDaemonAsync(DefaultDaemonAddress, proxy);
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
        if (NetworkMode == 2 && proxy.Length == 0)
        {
            SavedMessage = "Enter your SOCKS proxy as host:port (e.g. 127.0.0.1:9050), or choose another option. Not saved.";
            return;
        }

        if (proxy.Length > 0 && !IsValidProxy(proxy))
        {
            SavedMessage = "Proxy must be host:port (e.g. 127.0.0.1:9050 for Tor) — not saved.";
            return;
        }

        string torPath = (TorBinaryPath ?? string.Empty).Trim();

        try
        {
            AppSettings s = AppServices.Instance.Settings;
            s.WalletRpcBinaryPath = (WalletRpcBinaryPath ?? string.Empty).Trim();
            s.DefaultDaemonAddress = (DefaultDaemonAddress ?? string.Empty).Trim();
            s.DefaultNetworkIndex = NetworkIndex;
            s.AutoRefreshSeconds = AutoRefreshSeconds;
            s.AutoLockMinutes = AutoLockMinutes;
            s.ProxyAddress = NetworkMode == 0 ? string.Empty : proxy;
            bool torWasOn = s.UseBuiltInTor;
            string torPathBefore = s.TorBinaryPath;
            s.UseBuiltInTor = NetworkMode == 1;
            s.TorBinaryPath = torPath;
            AppServices.Instance.SaveSettings();
            ApplyTorSetting(torWasOn, torPathBefore != torPath);

            // Reflect any clamping back into the fields.
            AutoRefreshSeconds = s.AutoRefreshSeconds;
            AutoLockMinutes = s.AutoLockMinutes;
            SavedMessage = NetworkMode == 1 && TorInstalledOrOnPath().Length == 0 && torPath.Length == 0
                ? "Saved. Tor isn't installed yet: Download & install it under Network & privacy."
                : "Settings saved.";
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

        if (PasswordStrength.Evaluate(NewPassword).level < PasswordStrength.MinimumAccepted)
        {
            ChangePasswordResult = "That new password is too easy to guess — choose something longer or less predictable.";
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
            if (_profile is null)
            {
                // From the unlock screen: a lock's last save could otherwise land after this rewrite
                // and put the old password back, after "Password changed." was shown.
                await _closing;
            }

            // Argon2id at these parameters takes seconds — keep it OFF the UI thread so the
            // window never looks hung (a user who kills a "frozen" app mid-rewrite is the
            // failure mode the atomic vault write exists to survive, not to invite).
            // With a wallet open, the open session changes ITS OWN password only (the same rule
            // whichever password opened it). From the unlock screen, whichever profile the current
            // password opens. Either way the wording below is identical: an asymmetric rule or
            // message would tell a coercer holding the duress password that another wallet exists.
            bool changed = _profile is not null
                ? await _profile.ChangePasswordAsync(curChars, nextChars)
                : await Task.Run(() =>
                {
                    // Take ownership of the password chars FIRST (FromPassword zeroes them): if
                    // Load throws, the passwords must not be left un-zeroed on the heap.
                    using var cur = SecureBuffer.FromPassword(curChars);
                    using var next = SecureBuffer.FromPassword(nextChars);
                    VaultManager mgr = VaultManager.Load(AppServices.Instance.VaultPath);
                    return mgr.ChangePassword(cur, next);
                });

            if (changed)
            {
                ChangePasswordOk = true;
                ChangePasswordResult = "Password changed.";
                Log.Info("Vault password changed.");
            }
            else
            {
                ChangePasswordOk = false;
                ChangePasswordResult = _profile is not null
                    ? "That isn't the password of the wallet that's open."
                    : "That password didn't unlock this vault.";
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

            await _closing; // a lock's last save belongs in the backup
            byte[] bytes = File.ReadAllBytes(AppServices.Instance.VaultPath);
            VaultFile.Deserialize(bytes); // sanity: never export a corrupt vault as a "backup"
            PrivateFiles.WriteAllBytes(dest, bytes); // 0600, like the vault itself
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
            await _closing; // a lock's last save must not land on the restored file

            // Durable write of the replacement FIRST (flushed to disk), then a single atomic
            // swap — matching VaultManager.Persist's discipline. The live vault path holds
            // either the old vault or the new one at every instant, even across a crash or
            // power loss; there is never a window with NO vault at the live path.
            using (FileStream fs = PrivateFiles.OpenWrite(tmp))
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

    /// <summary>Start, restart or stop the built-in Tor to match what was just saved.</summary>
    private void ApplyTorSetting(bool wasOn, bool binaryChanged)
    {
        if (NetworkMode == 1)
        {
            if (!wasOn || binaryChanged || Tor.State is TorState.Off or TorState.Failed)
            {
                _ = RestartTorCommand.ExecuteAsync(null);
            }
        }
        else if (Tor.State != TorState.Off)
        {
            _ = Tor.StopAsync();
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
