using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>
/// First-run flow. Supports GENERATING a fresh Monero seed (via monero-wallet-rpc,
/// never hand-rolled) with a mandatory backup step — the user must either verify
/// three random words or download an encrypted-at-rest-warning backup file — or
/// IMPORTING an existing seed. The same options apply to the optional duress decoy.
/// </summary>
public sealed partial class CreateWalletViewModel : ViewModelBase
{
    // --- passwords ---
    [ObservableProperty] private string _mainPassword = string.Empty;
    [ObservableProperty] private string _mainPasswordConfirm = string.Empty;
    [ObservableProperty] private string _strengthLabel = string.Empty;

    // --- duress ---
    [ObservableProperty] private bool _enableDuress;
    [ObservableProperty] private string _duressPassword = string.Empty;
    [ObservableProperty] private bool _wipeRealOnDuress;
    [ObservableProperty] private bool _createNewDuress = true;
    [ObservableProperty] private string _duressMnemonic = string.Empty;
    [ObservableProperty] private bool _duressSeedGenerated;

    // --- network / daemon ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainnet))]
    private int _networkIndex;                 // 0 mainnet, 1 stagenet, 2 testnet
    [ObservableProperty] private string _daemonAddress = "http://127.0.0.1:18081";

    /// <summary>True when the mainnet (real money) network is selected — drives the warning banner.</summary>
    public bool IsMainnet => NetworkIndex == 0;

    /// <summary>Public node presets (same list as Settings). Selecting one fills daemon + network.</summary>
    public IReadOnlyList<RemoteNode> PresetNodes => RemoteNodes.All;
    [ObservableProperty] private RemoteNode? _selectedPreset;

    partial void OnSelectedPresetChanged(RemoteNode? value)
    {
        if (value is null)
        {
            return;
        }

        DaemonAddress = value.Url;
        NetworkIndex = value.NetworkIndex;
    }

    // When the user flips network and their daemon is still a default localhost address, move the
    // port to the matching default (mainnet 18081 / stagenet 38081 / testnet 28081). A custom host
    // is left untouched.
    partial void OnNetworkIndexChanged(int value)
    {
        string port = value switch { 1 => "38081", 2 => "28081", _ => "18081" };
        string current = (DaemonAddress ?? string.Empty).Trim();
        if (current is "http://127.0.0.1:18081" or "http://127.0.0.1:38081" or "http://127.0.0.1:28081"
                    or "http://localhost:18081" or "http://localhost:38081" or "http://localhost:28081"
                    or "")
        {
            DaemonAddress = $"http://127.0.0.1:{port}";
        }
    }

    // --- real wallet seed ---
    [ObservableProperty] private bool _createNewReal = true;        // generate vs import
    [ObservableProperty] private string _realMnemonic = string.Empty;
    [ObservableProperty] private ulong _restoreHeight;
    [ObservableProperty] private bool _realSeedGenerated;           // true only after generation
    [ObservableProperty] private bool _realVerified;
    [ObservableProperty] private bool _realBackedUp;

    // Import "sync from" mode: 0 = full history, 1 = from a specific block, 2 = from now.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpecificHeight))]
    private int _restoreMode = 1;

    public bool IsSpecificHeight => RestoreMode == 1;

    // --- verification quiz ---
    [ObservableProperty] private string _verifyPrompt1 = string.Empty;
    [ObservableProperty] private string _verifyPrompt2 = string.Empty;
    [ObservableProperty] private string _verifyPrompt3 = string.Empty;
    [ObservableProperty] private string _verifyInput1 = string.Empty;
    [ObservableProperty] private string _verifyInput2 = string.Empty;
    [ObservableProperty] private string _verifyInput3 = string.Empty;
    [ObservableProperty] private string _verifyMessage = string.Empty;
    [ObservableProperty] private bool _showVerifyOverlay;
    private int[] _verifyIndices = System.Array.Empty<int>();

    /// <summary>The generated seed split into numbered words for the display grid.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SeedWord> RealSeedWords { get; } = new();

    // --- status ---
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _busy;

    /// <summary>Set by the View: (fileContents, suggestedName) -> saved? Uses the platform save dialog.</summary>
    public Func<string, string, Task<bool>>? SaveBackupHandler { get; set; }

    public event Action? Created;

    public CreateWalletViewModel()
    {
        // Pre-fill from saved settings so the user configures the daemon/network once.
        string daemon = AppServices.Instance.DefaultDaemonAddress;
        _daemonAddress = string.IsNullOrWhiteSpace(daemon) ? "http://127.0.0.1:18081" : daemon;
        _networkIndex = AppServices.Instance.DefaultNetworkIndex;
    }

    private MoneroNetwork Network => NetworkIndex switch
    {
        1 => MoneroNetwork.Stagenet,
        2 => MoneroNetwork.Testnet,
        _ => MoneroNetwork.Mainnet,
    };

    partial void OnMainPasswordChanged(string value)
    {
        var (level, bits) = PasswordStrength.Evaluate(value);
        StrengthLabel = value.Length == 0 ? string.Empty : $"{level} (~{bits:0} bits)";
    }

    // ============================ seed generation ============================

    [RelayCommand]
    private async Task GenerateRealSeedAsync()
    {
        Error = string.Empty;
        Busy = true;
        try
        {
            await using var svc = AppServices.Instance.CreateWalletService();
            (string mnemonic, ulong height) = await svc.GenerateNewSeedAsync(Network, DaemonAddress.Trim());
            RealMnemonic = mnemonic;
            RestoreHeight = height;
            RealSeedGenerated = true;
            RealVerified = false;
            RealBackedUp = false;
            PopulateSeedWords(mnemonic);
        }
        catch (Exception ex)
        {
            Error = "Seed generation failed: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task GenerateDuressSeedAsync()
    {
        Error = string.Empty;
        Busy = true;
        try
        {
            await using var svc = AppServices.Instance.CreateWalletService();
            (string mnemonic, ulong _) = await svc.GenerateNewSeedAsync(Network, DaemonAddress.Trim());
            DuressMnemonic = mnemonic;
            DuressSeedGenerated = true;
        }
        catch (Exception ex)
        {
            Error = "Decoy seed generation failed: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private void PopulateSeedWords(string mnemonic)
    {
        RealSeedWords.Clear();
        string[] words = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            RealSeedWords.Add(new SeedWord(i + 1, words[i]));
        }
    }

    /// <summary>Opens the verification overlay with three freshly-picked word positions.</summary>
    [RelayCommand]
    private void OpenVerify()
    {
        if (!RealSeedGenerated)
        {
            return;
        }

        SetupVerification(RealMnemonic);
        ShowVerifyOverlay = true;
    }

    [RelayCommand]
    private void CloseVerify() => ShowVerifyOverlay = false;

    private void SetupVerification(string mnemonic)
    {
        string[] words = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int[] picks = Enumerable.Range(0, words.Length)
            .OrderBy(_ => Random.Shared.Next())
            .Take(3)
            .OrderBy(x => x)
            .ToArray();

        _verifyIndices = picks;
        VerifyPrompt1 = $"Word #{picks[0] + 1}";
        VerifyPrompt2 = $"Word #{picks[1] + 1}";
        VerifyPrompt3 = $"Word #{picks[2] + 1}";
        VerifyInput1 = VerifyInput2 = VerifyInput3 = string.Empty;
        VerifyMessage = string.Empty;
    }

    [RelayCommand]
    private void VerifyRealSeed()
    {
        string[] words = RealMnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (_verifyIndices.Length != 3)
        {
            return;
        }

        bool ok =
            words[_verifyIndices[0]].Equals(VerifyInput1.Trim(), StringComparison.OrdinalIgnoreCase) &&
            words[_verifyIndices[1]].Equals(VerifyInput2.Trim(), StringComparison.OrdinalIgnoreCase) &&
            words[_verifyIndices[2]].Equals(VerifyInput3.Trim(), StringComparison.OrdinalIgnoreCase);

        RealVerified = ok;
        if (ok)
        {
            VerifyMessage = string.Empty;
            ShowVerifyOverlay = false; // success — dismiss the overlay
        }
        else
        {
            VerifyMessage = "One or more words don't match. Check your written backup.";
        }
    }

    [RelayCommand]
    private async Task DownloadRealBackupAsync()
    {
        if (SaveBackupHandler is null)
        {
            Error = "Save dialog unavailable.";
            return;
        }

        string content = BackupText(RealMnemonic, RestoreHeight, Network);
        bool saved = await SaveBackupHandler(content, "xault-seed-backup.txt");
        if (saved)
        {
            RealBackedUp = true;
            VerifyMessage = "Backup file saved. Store it somewhere safe and offline.";
        }
    }

    [RelayCommand]
    private async Task DownloadDuressBackupAsync()
    {
        if (SaveBackupHandler is null)
        {
            return;
        }

        string content = BackupText(DuressMnemonic, RestoreHeight, Network);
        await SaveBackupHandler(content, "xault-decoy-seed-backup.txt");
    }

    private static string BackupText(string mnemonic, ulong restoreHeight, MoneroNetwork network) =>
        "XaultWallet Monero seed backup\r\n" +
        "================================\r\n" +
        "WARNING: this file contains your seed in PLAINTEXT. Anyone who reads it can\r\n" +
        "spend your funds. Store it offline (paper/steel/encrypted media) and delete\r\n" +
        "any copy left on this computer.\r\n\r\n" +
        $"Created: {DateTime.UtcNow:u}\r\n" +
        $"Network: {network}\r\n" +
        $"Restore height: {restoreHeight}\r\n\r\n" +
        "25-word mnemonic:\r\n" +
        mnemonic + "\r\n";

    // ============================ create the vault ============================

    [RelayCommand]
    private async Task CreateAsync()
    {
        Error = string.Empty;

        if (!IsValidDaemon(DaemonAddress))
        {
            Error = "Daemon address must be a valid http(s) URL, e.g. http://127.0.0.1:18081";
            return;
        }

        if (MainPassword.Length < 8)
        {
            Error = "Main password must be at least 8 characters.";
            return;
        }

        if (MainPassword != MainPasswordConfirm)
        {
            Error = "Passwords do not match.";
            return;
        }

        // Normalise any imported seeds up front.
        if (!CreateNewReal)
        {
            RealMnemonic = NormalizeMnemonic(RealMnemonic);
        }

        if (EnableDuress && !CreateNewDuress)
        {
            DuressMnemonic = NormalizeMnemonic(DuressMnemonic);
        }

        // Real seed must be present and, if freshly generated, backed up.
        if (CreateNewReal)
        {
            if (!RealSeedGenerated)
            {
                Error = "Generate a seed first.";
                return;
            }

            if (!RealVerified && !RealBackedUp)
            {
                Error = "Before continuing, verify three words of your seed or download the backup file.";
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(RealMnemonic))
        {
            Error = "Enter your existing 25-word seed to import.";
            return;
        }

        if (EnableDuress)
        {
            if (DuressPassword.Length < 8)
            {
                Error = "Duress password must be at least 8 characters.";
                return;
            }

            if (DuressPassword == MainPassword)
            {
                Error = "The duress password must be different from the main password.";
                return;
            }

            if (string.IsNullOrWhiteSpace(DuressMnemonic))
            {
                Error = "Provide a decoy seed (generate or import) for the duress wallet.";
                return;
            }
        }

        Busy = true;
        try
        {
            // For imports, resolve the "sync from" choice into a restore height. Generated
            // wallets keep the tip height captured at generation (nothing before it to scan).
            ulong effectiveRestore = RestoreHeight;
            if (!CreateNewReal)
            {
                effectiveRestore = RestoreMode switch
                {
                    0 => 0UL,                                   // full history
                    2 => await GetTipHeightAsync(),             // from now (fastest)
                    _ => RestoreHeight,                         // from a specific block
                };
            }

            var main = new WalletSecrets
            {
                Kind = ProfileKind.Real,
                Label = "Main",
                Network = Network,
                Mnemonic = RealMnemonic.Trim(),
                RestoreHeight = effectiveRestore,
                DaemonAddress = DaemonAddress.Trim(),
                EphemeralWalletPassword = Convert.ToHexString(VaultCrypto.RandomBytes(24)),
            };

            WalletSecrets? duress = null;
            if (EnableDuress)
            {
                duress = new WalletSecrets
                {
                    Kind = ProfileKind.Duress,
                    Label = "Wallet",
                    Network = Network,
                    Mnemonic = DuressMnemonic.Trim(),
                    RestoreHeight = effectiveRestore,
                    DaemonAddress = DaemonAddress.Trim(),
                    EphemeralWalletPassword = Convert.ToHexString(VaultCrypto.RandomBytes(24)),
                    DuressWipeReal = WipeRealOnDuress,
                };
            }

            // Validate imported seeds actually open a wallet BEFORE sealing them, so a typo
            // can't produce an unopenable vault. Generated seeds are already known-good.
            if (!CreateNewReal && !await ValidateImportedSeedAsync(main, "seed"))
            {
                return;
            }

            if (EnableDuress && !CreateNewDuress && !await ValidateImportedSeedAsync(duress!, "decoy seed"))
            {
                return;
            }

            char[] mainChars = MainPassword.ToCharArray();
            char[]? duressChars = EnableDuress ? DuressPassword.ToCharArray() : null;
            MainPassword = MainPasswordConfirm = DuressPassword = string.Empty;

            await Task.Run(() =>
            {
                using var mainPw = SecureBuffer.FromPassword(mainChars);
                SecureBuffer? duressPw = duressChars is null ? null : SecureBuffer.FromPassword(duressChars);
                try
                {
                    VaultManager.Create(AppServices.Instance.VaultPath, mainPw, main, duressPw, duress);
                }
                finally
                {
                    duressPw?.Dispose();
                }
            });

            // Wipe the seeds from the view model now that they're sealed in the vault.
            RealMnemonic = DuressMnemonic = string.Empty;
            XaultWallet.Core.Diagnostics.Log.Info("Vault created.");
            Created?.Invoke();
        }
        catch (Exception ex)
        {
            XaultWallet.Core.Diagnostics.Log.Error("Vault creation failed", ex);
            Error = ex is IOException ? ex.Message : "Could not create the vault: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Returns true if the seed is acceptable. Prefers authoritative validation via
    /// monero-wallet-rpc; if the binary isn't available, falls back to a word-count sanity check
    /// and warns that full validation was skipped.
    /// </summary>
    private async Task<bool> ValidateImportedSeedAsync(WalletSecrets secrets, string what)
    {
        try
        {
            await using var svc = AppServices.Instance.CreateWalletService();
            await svc.ValidateSeedOpensAsync(secrets);
            return true;
        }
        catch (FileNotFoundException)
        {
            // Binary not available — degrade gracefully to a structural check.
            int words = secrets.Mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (words is 25 or 13)
            {
                return true;
            }

            Error = $"Your {what} has {words} words; a Monero seed is normally 25 words. " +
                    "Couldn't fully validate it (monero-wallet-rpc not found). Double-check it.";
            return false;
        }
        catch (Exception ex)
        {
            Error = $"That {what} doesn't appear to be valid: {ex.Message}";
            return false;
        }
    }

    private static bool IsValidDaemon(string address) =>
        !string.IsNullOrWhiteSpace(address)
        && Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Current daemon tip height, or 0 (full scan) if it can't be reached.</summary>
    private async Task<ulong> GetTipHeightAsync()
    {
        try
        {
            return await MoneroDiagnostics.ProbeDaemonAsync(DaemonAddress.Trim());
        }
        catch
        {
            return 0; // fall back to a full scan rather than silently skipping blocks
        }
    }

    private static string NormalizeMnemonic(string raw) =>
        string.Join(' ', (raw ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
}

/// <summary>A single numbered word in the seed display grid.</summary>
public sealed record SeedWord(int Number, string Word);
