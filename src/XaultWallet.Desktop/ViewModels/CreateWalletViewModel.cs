using System.Linq;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;
// The VM's DaemonAddress *property* shadows the Core helper class of the same name.
using DaemonUrl = XaultWallet.Core.Monero.DaemonAddress;

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
    /// <summary>Seed-offset passphrase for an IMPORTED decoy seed. Ignored for a generated decoy.</summary>
    [ObservableProperty] private string _duressSeedOffset = string.Empty;

    /// <summary>Chain tip captured when the decoy seed was GENERATED — the decoy's own "newest
    /// block only" starting point. A brand-new seed has no earlier history, so scanning from its
    /// generation moment is always correct and avoids a pointless full-chain scan.</summary>
    private ulong _duressRestoreHeight;

    // --- network / daemon ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainnet))]
    private int _networkIndex;                 // 0 mainnet, 1 stagenet, 2 testnet
    [ObservableProperty] private string _daemonAddress = DefaultLocalDaemon(1);

    private static string DefaultLocalDaemon(int networkIndex) =>
        $"http://127.0.0.1:{networkIndex switch { 1 => "38081", 2 => "28081", _ => "18081" }}";

    /// <summary>True when the mainnet (real money) network is selected — drives the warning banner.
    /// Not for the user's own local regtest chain, which uses mainnet addresses but has no real funds.</summary>
    public bool IsMainnet => NetworkIndex == 0 && !IsLocalTestChain;

    /// <summary>The node is a private test chain on this computer (local <c>monerod --regtest</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMainnet))]
    private bool _isLocalTestChain;

    private CancellationTokenSource? _chainProbe;

    partial void OnDaemonAddressChanged(string value) => _ = ProbeTestChainAsync(value);

    /// <summary>Ask the node what chain it is on, debounced while the address is being typed.</summary>
    private async Task ProbeTestChainAsync(string address)
    {
        _chainProbe?.Cancel();
        var cts = _chainProbe = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token);
            bool testChain = await MoneroDiagnostics.IsLocalTestChainAsync(address, await AppServices.Instance.GetNetworkProxyAsync(address, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                IsLocalTestChain = testChain;
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer address
        }
        catch (TorNotReadyException)
        {
            // Built-in Tor isn't connected: the node can't be asked, so it isn't a test chain to us.
        }
    }

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

        // Chain-specific state must not survive a network switch. A generation tip captured on the
        // OLD chain can sit far above the NEW chain's tip; sealing it would make incoming funds
        // invisible until that chain grows past it (possibly never). Generated seeds must be
        // re-generated on the new network (their backup .txt also names the network); typed import
        // seeds are kept (a mnemonic is network-agnostic) but their height resets to full history.
        RealSeedGenerated = false;
        RealVerified = false;
        RealBackedUp = false;
        RealSeedWords.Clear();
        if (CreateNewReal)
        {
            RealMnemonic = string.Empty;
        }

        DuressSeedGenerated = false;
        if (CreateNewDuress)
        {
            DuressMnemonic = string.Empty;
        }

        _duressRestoreHeight = 0;
        _generatedRestoreHeight = 0;
        RestoreHeightText = string.Empty;
        RestoreMode = 0;
    }

    // --- real wallet seed ---
    [ObservableProperty] private bool _createNewReal = true;        // generate vs import
    [ObservableProperty] private string _realMnemonic = string.Empty;
    /// <summary>Seed-offset passphrase for an IMPORTED real seed. Ignored (forced empty) for a generated seed.</summary>
    [ObservableProperty] private string _realSeedOffset = string.Empty;

    /// <summary>Import "from a specific block": the height exactly as typed. Parsed strictly (digits,
    /// optional thousands separators) — a culture-bound number box silently dropped "2.800.000".</summary>
    [ObservableProperty] private string _restoreHeightText = string.Empty;

    /// <summary>The generated real seed's own restore height (daemon tip before generation − margin).</summary>
    private ulong _generatedRestoreHeight;
    [ObservableProperty] private bool _realSeedGenerated;           // true only after generation
    [ObservableProperty] private bool _realVerified;
    [ObservableProperty] private bool _realBackedUp;

    // Import "sync from" mode: 0 = full history, 1 = from a specific block, 2 = from now.
    // Defaults to FULL HISTORY — the safe choice for an imported seed, which may hold funds of any
    // age; the faster options are a deliberate opt-in. (Generated seeds don't use this selector at
    // all: they always scan from their own generation-time tip.)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpecificHeight))]
    private int _restoreMode;

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

    // --- imported-seed address confirmation (hard stop before sealing an import) ---
    [ObservableProperty] private bool _showAddressConfirm;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfirmRealAddress))]
    private string _confirmRealAddress = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfirmDuressAddress))]
    private string _confirmDuressAddress = string.Empty;

    public bool HasConfirmRealAddress => !string.IsNullOrEmpty(ConfirmRealAddress);
    public bool HasConfirmDuressAddress => !string.IsNullOrEmpty(ConfirmDuressAddress);

    // Everything needed to seal, snapshotted in phase 1 so nothing the user touches under the
    // address-confirm overlay (e.g. toggling the duress checkbox) can change what gets sealed.
    private WalletSecrets? _pendingMain;
    private WalletSecrets? _pendingDuress;
    private char[]? _pendingMainPassword;
    private char[]? _pendingDuressPassword;

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
        _networkIndex = AppServices.Instance.DefaultNetworkIndex;
        _daemonAddress = string.IsNullOrWhiteSpace(daemon) ? DefaultLocalDaemon(_networkIndex) : daemon;
        _ = ProbeTestChainAsync(_daemonAddress);
    }

    private MoneroNetwork Network => NetworkIndex switch
    {
        1 => MoneroNetwork.Stagenet,
        2 => MoneroNetwork.Testnet,
        _ => MoneroNetwork.Mainnet,
    };

    // Strength meter: 0..5 segments plus a colour band (weak / fair / strong).
    [ObservableProperty] private int _strengthScore;
    [ObservableProperty] private bool _strengthIsFair;
    [ObservableProperty] private bool _strengthIsStrong;

    partial void OnMainPasswordChanged(string value)
    {
        var (level, bits) = PasswordStrength.Evaluate(value);
        StrengthScore = (int)level;
        StrengthIsFair = level == StrengthLevel.Fair;
        StrengthIsStrong = level >= StrengthLevel.Strong;
        StrengthLabel = value.Length == 0
            ? string.Empty
            : level < PasswordStrength.MinimumAccepted
                ? "Too easy to guess"
                : $"{LevelName(level)} · about {bits.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} bits";
    }

    internal static string LevelName(StrengthLevel level) => level switch
    {
        StrengthLevel.VeryWeak => "Very weak",
        StrengthLevel.Weak => "Weak",
        StrengthLevel.Fair => "Fair",
        StrengthLevel.Strong => "Strong",
        StrengthLevel.VeryStrong => "Very strong",
        _ => string.Empty,
    };

    // Switching between Generate and Import must FULLY reset the seed state for that profile.
    // Otherwise a seed left over from the other mode keeps its old provenance while the mode flips:
    // e.g. generate a seed, switch to Import, then type an offset — CreateNewReal is now false, so
    // SeedOffsetPolicy would seal that GENERATED seed WITH an offset and unlock would restore a
    // different, empty wallet than the backup (silent fund loss). Clearing on switch guarantees
    // CreateNewReal/CreateNewDuress always agree with what the seed actually is.
    partial void OnCreateNewRealChanged(bool value)
    {
        RealMnemonic = string.Empty;
        RealSeedOffset = string.Empty;
        RealSeedGenerated = false;
        RealVerified = false;
        RealBackedUp = false;
        RealSeedWords.Clear();
        // Height state belongs to the seed it was captured/typed for — reset with it, or a
        // generation-time tip would leak into the import height box (and vice versa).
        _generatedRestoreHeight = 0;
        RestoreHeightText = string.Empty;
        RestoreMode = 0;
        Error = string.Empty;
    }

    partial void OnCreateNewDuressChanged(bool value)
    {
        DuressMnemonic = string.Empty;
        DuressSeedOffset = string.Empty;
        DuressSeedGenerated = false;
        _duressRestoreHeight = 0;
        Error = string.Empty;
    }

    // ============================ seed generation ============================

    [RelayCommand]
    private async Task GenerateRealSeedAsync()
    {
        Error = string.Empty;
        Busy = true;
        try
        {
            (string mnemonic, ulong height) = await GenerateSeedAsync();
            RealMnemonic = mnemonic;
            _generatedRestoreHeight = height;
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
            (string mnemonic, ulong height) = await GenerateSeedAsync();
            DuressMnemonic = mnemonic;
            _duressRestoreHeight = height; // the decoy's OWN tip — not the real wallet's height
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

    /// <summary>A new seed from monero-wallet-rpc, in a backend that the app's exit stops.</summary>
    private Task<(string mnemonic, ulong height)> GenerateSeedAsync()
    {
        MoneroNetwork network = Network;
        string daemon = DaemonAddress.Trim();
        return AppServices.Instance.TemporaryBackends.RunAsync(async ct =>
        {
            await using var svc = AppServices.Instance.CreateWalletService(await AppServices.Instance.GetNetworkProxyAsync(daemon, ct));
            return await svc.GenerateNewSeedAsync(network, daemon, ct);
        });
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
        // CSPRNG: Random.Shared is not cryptographic and must not pick which seed words the user
        // is asked to type back.
        int[] order = Enumerable.Range(0, words.Length).ToArray();
        RandomNumberGenerator.Shuffle(order.AsSpan());
        int[] picks = order.Take(3).OrderBy(x => x).ToArray();

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

        // The create form stays keyboard-reachable under this overlay: flipping the network or
        // the generate/import toggle clears RealMnemonic but leaves _verifyIndices populated,
        // so the indices can point past the (now empty) word list.
        if (_verifyIndices.Length != 3 || _verifyIndices.Any(i => i >= words.Length))
        {
            VerifyMessage = "The seed changed — generate it again before verifying.";
            RealVerified = false;
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

        string content = BackupText(RealMnemonic, _generatedRestoreHeight, Network);
        bool saved = await SaveBackupHandler(content, SeedBackupFile.SuggestedName(DateTime.Now));
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

        // The decoy backup records the DECOY's own generation height, not the real wallet's. Its
        // suggested file name follows the same scheme as the real one: a name must not say "decoy".
        string content = BackupText(DuressMnemonic, _duressRestoreHeight, Network);
        await SaveBackupHandler(content, SeedBackupFile.SuggestedName(DateTime.Now));
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

        // If an address confirmation is already pending, ignore a re-trigger (the form stays
        // keyboard-reachable under the overlay). The pending seal is driven by the overlay buttons.
        if (ShowAddressConfirm)
        {
            return;
        }

        if (!DaemonUrl.IsValid(DaemonAddress))
        {
            Error = "Daemon address must be a valid http(s) URL, e.g. http://127.0.0.1:18081";
            return;
        }

        if (MainPassword.Length < 8)
        {
            Error = "Main password must be at least 8 characters.";
            return;
        }

        if (PasswordStrength.Evaluate(MainPassword).level < PasswordStrength.MinimumAccepted)
        {
            Error = "That password is too easy to guess (a common word, sequence or repeated pattern). " +
                    "Anyone who copies the vault file can try passwords offline — choose something longer or less predictable.";
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

            if (PasswordStrength.Evaluate(DuressPassword).level < PasswordStrength.MinimumAccepted)
            {
                Error = "The duress password is too easy to guess. It should look like a real password: " +
                        "a guessable one lets anyone holding the vault file open the decoy.";
                return;
            }

            // Mirror the real seed's provenance gate: in generate mode the decoy must actually
            // have been generated (so its height/provenance state is consistent), not merely
            // present in the text box.
            if (CreateNewDuress && !DuressSeedGenerated)
            {
                Error = "Generate a decoy seed first.";
                return;
            }

            if (string.IsNullOrWhiteSpace(DuressMnemonic))
            {
                Error = "Provide a decoy seed (generate or import) for the duress wallet.";
                return;
            }

            // Same seed in both slots = the "decoy" opens the real funds, silently defeating
            // the entire duress feature. (VaultManager.Create enforces this too; catching it
            // here gives a friendlier message before any RPC work.)
            if (NormalizeMnemonic(DuressMnemonic) == NormalizeMnemonic(RealMnemonic))
            {
                Error = "The decoy seed must be different from the real wallet's seed.";
                return;
            }
        }

        Busy = true;
        try
        {
            // Restore-height rules — consistent by seed provenance:
            //   GENERATED seed (real or decoy): newest block only, from ITS OWN generation-time
            //     tip. A brand-new seed cannot have earlier history, so this is always correct
            //     and skips the pointless full-chain scan. If the daemon was unreachable at
            //     generation (captured 0), re-probe now; a fresh seed created "blind" still has
            //     no history before this moment.
            //   IMPORTED real seed: the user chooses via the "sync from" selector.
            //   IMPORTED decoy seed: always full history — it may hold funds older than anything
            //     on this screen, and a decoy that silently hides its own balance is broken.
            // One tip probe serves fallback, "from now", and a safety clamp: a generated seed's
            // captured height must never exceed the chain's current tip (wallet-rpc can
            // date-estimate above the real tip on test networks), or funds would be skipped.
            bool needTip = CreateNewReal
                           || (!CreateNewReal && RestoreMode is 1 or 2)
                           || (EnableDuress && CreateNewDuress);
            ulong typedHeight = 0;
            if (!CreateNewReal && RestoreMode == 1 && !BlockHeight.TryParse(RestoreHeightText, out typedHeight))
            {
                Error = "Enter the block height to scan from, e.g. 3150000.";
                return;
            }

            ulong tipNow = needTip ? await GetTipHeightAsync() : 0UL;
            static ulong ClampToTip(ulong captured, ulong tip) => tip == 0 ? captured : Math.Min(captured, tip);

            // "Newest blocks only" never means the bare tip: the node's claim is capped by the
            // clock-based chain estimate and backed off a safety margin (RestoreHeights.ForNewSeed).
            ulong recentStart = tipNow == 0 ? 0 : RestoreHeights.ForNewSeed(tipNow, Network, DateTimeOffset.UtcNow);

            ulong realRestore;
            if (CreateNewReal)
            {
                realRestore = _generatedRestoreHeight != 0 ? ClampToTip(_generatedRestoreHeight, tipNow) : recentStart;
            }
            else
            {
                realRestore = RestoreMode switch
                {
                    0 => 0UL,                                   // full history (safest for imports)
                    2 => recentStart,                           // from now (new seeds only)
                    _ => typedHeight,                           // from a specific block
                };

                // A typo'd restore height above the chain tip scans NOTHING: zero balance, no
                // error, and the user concludes the seed is bad. Catch it while the tip is known.
                if (RestoreMode == 1 && tipNow > 0 && typedHeight > tipNow)
                {
                    Error = $"Restore height {typedHeight:N0} is beyond the current chain tip ({tipNow:N0}). Check for a typo.";
                    return;
                }
            }

            var main = new WalletSecrets
            {
                Network = Network,
                Mnemonic = RealMnemonic.Trim(),
                // A generated seed NEVER carries an offset (that would restore a different, empty
                // wallet = fund loss); an imported seed keeps exactly what the user supplied.
                SeedOffset = SeedOffsetPolicy.ForSeed(wasGenerated: CreateNewReal, RealSeedOffset),
                RestoreHeight = realRestore,
                DaemonAddress = DaemonAddress.Trim(),
                EphemeralWalletPassword = Convert.ToHexString(VaultCrypto.RandomBytes(24)),
            };

            WalletSecrets? duress = null;
            if (EnableDuress)
            {
                // The decoy gets its OWN height, never the real wallet's: a generated decoy scans
                // from its own generation tip (clamped to the current chain tip); an imported decoy
                // scans full history so any existing funds are guaranteed to appear.
                ulong duressRestore = CreateNewDuress
                    ? (_duressRestoreHeight != 0 ? ClampToTip(_duressRestoreHeight, tipNow) : recentStart)
                    : 0UL;

                duress = new WalletSecrets
                {
                    Network = Network,
                    Mnemonic = DuressMnemonic.Trim(),
                    SeedOffset = SeedOffsetPolicy.ForSeed(wasGenerated: CreateNewDuress, DuressSeedOffset),
                    RestoreHeight = duressRestore,
                    DaemonAddress = DaemonAddress.Trim(),
                    EphemeralWalletPassword = Convert.ToHexString(VaultCrypto.RandomBytes(24)),
                    WipeOtherSlotOnUnlock = WipeRealOnDuress,
                };
            }

            // Validate imported seeds actually open a wallet BEFORE sealing them (a typo can't
            // produce an unopenable vault) and capture the derived primary address to echo back.
            // Generated seeds are already known-good and their seed was shown and backed up.
            string? realAddr = null;
            if (!CreateNewReal)
            {
                (bool ok, realAddr) = await ValidateImportedSeedAddressAsync(main, "seed");
                if (!ok)
                {
                    return;
                }
            }

            string? duressAddr = null;
            if (EnableDuress && !CreateNewDuress)
            {
                (bool ok, duressAddr) = await ValidateImportedSeedAddressAsync(duress!, "decoy seed");
                if (!ok)
                {
                    return;
                }
            }

            // Hard stop for imports: a wrong seed or seed-offset opens a valid but DIFFERENT, empty
            // wallet with no error. Echo the derived address(es) and require the user to confirm the
            // match BEFORE sealing. Imports without monero-wallet-rpc never reach here (Validate
            // returns false); generated-only wallets have nothing to echo and seal directly.
            if (realAddr is not null || duressAddr is not null)
            {
                // Snapshot the seal inputs and clear the live password fields. Phase 2 seals from
                // this snapshot only, so nothing changed under the overlay can affect the result.
                _pendingMain = main;
                _pendingDuress = duress;
                _pendingMainPassword = MainPassword.ToCharArray();
                _pendingDuressPassword = EnableDuress ? DuressPassword.ToCharArray() : null;
                MainPassword = MainPasswordConfirm = DuressPassword = string.Empty;

                ConfirmRealAddress = realAddr ?? string.Empty;
                ConfirmDuressAddress = duressAddr ?? string.Empty;
                ShowAddressConfirm = true;
                return; // wait for ConfirmAddresses / CancelAddressConfirm
            }

            // Generated-only wallet: nothing to echo, seal directly from a fresh snapshot.
            char[] mainChars = MainPassword.ToCharArray();
            char[]? duressChars = EnableDuress ? DuressPassword.ToCharArray() : null;
            MainPassword = MainPasswordConfirm = DuressPassword = string.Empty;
            await SealVaultAsync(main, duress, mainChars, duressChars);
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

    /// <summary>The user confirmed the echoed address matches — seal the validated, pending vault
    /// from the phase-1 snapshot.</summary>
    [RelayCommand]
    private async Task ConfirmAddressesAsync()
    {
        ShowAddressConfirm = false;
        WalletSecrets? main = _pendingMain;
        WalletSecrets? duress = _pendingDuress;
        char[]? mainChars = _pendingMainPassword;
        char[]? duressChars = _pendingDuressPassword;

        // Hand ownership of the password snapshot to the seal (it zeroes them). Never seal with a
        // missing password snapshot.
        _pendingMainPassword = null;
        _pendingDuressPassword = null;
        if (main is null || mainChars is null)
        {
            WipeChars(mainChars);
            WipeChars(duressChars);
            _pendingMain = _pendingDuress = null;
            return; // nothing pending / snapshot lost — do not seal
        }

        Busy = true;
        try
        {
            await SealVaultAsync(main, duress, mainChars, duressChars);
        }
        catch (Exception ex)
        {
            XaultWallet.Core.Diagnostics.Log.Error("Vault creation failed", ex);
            Error = ex is IOException ? ex.Message : "Could not create the vault: " + ex.Message;
        }
        finally
        {
            Busy = false;
            _pendingMain = null;
            _pendingDuress = null;
            ConfirmRealAddress = ConfirmDuressAddress = string.Empty;
        }
    }

    /// <summary>The address did NOT match — abort without sealing. Seed fields are left intact so the
    /// user can correct the seed/offset and try again; the password snapshot is wiped.</summary>
    [RelayCommand]
    private void CancelAddressConfirm()
    {
        ShowAddressConfirm = false;
        _pendingMain = null;
        _pendingDuress = null;
        WipeChars(_pendingMainPassword);
        WipeChars(_pendingDuressPassword);
        _pendingMainPassword = null;
        _pendingDuressPassword = null;
        ConfirmRealAddress = ConfirmDuressAddress = string.Empty;
    }

    private static void WipeChars(char[]? chars)
    {
        if (chars is not null)
        {
            Array.Clear(chars);
        }
    }

    /// <summary>Seal the already-validated secrets into the vault and signal completion. This is the
    /// ONLY path to VaultManager.Create — reached directly for generated-only wallets, or after the
    /// address-confirmation hard stop for imports. Consumes (and zeroes) the password char arrays.</summary>
    private async Task SealVaultAsync(WalletSecrets main, WalletSecrets? duress, char[] mainChars, char[]? duressChars)
    {
        await Task.Run(() =>
        {
            using var mainPw = SecureBuffer.FromPassword(mainChars); // FromPassword zeroes mainChars
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

        // Wipe the seeds and offsets from the view model now that they're sealed in the vault.
        RealMnemonic = DuressMnemonic = string.Empty;
        RealSeedOffset = DuressSeedOffset = string.Empty;
        RealSeedWords.Clear(); // the numbered-word display grid holds the full seed too
        XaultWallet.Core.Diagnostics.Log.Info("Vault created.");
        Created?.Invoke();
    }

    /// <summary>
    /// Validates an imported seed by actually opening it via monero-wallet-rpc, returning
    /// (true, primaryAddress) so the caller can echo the address for a confirmation hard stop.
    /// Without the binary the import is refused — a word-count check cannot catch a wrong seed
    /// or seed-offset. Returns (false, null) and sets Error on an invalid seed or a missing binary.
    /// </summary>
    private async Task<(bool ok, string? address)> ValidateImportedSeedAddressAsync(WalletSecrets secrets, string what)
    {
        try
        {
            string address = await AppServices.Instance.TemporaryBackends.RunAsync(async ct =>
            {
                await using var svc = AppServices.Instance.CreateWalletService(await AppServices.Instance.GetNetworkProxyAsync(secrets.DaemonAddress, ct));
                return await svc.ValidateSeedOpensAsync(secrets, ct);
            });
            return (true, address);
        }
        catch (FileNotFoundException)
        {
            // Same posture as AddWalletViewModel: importing without the binary would skip the
            // address echo and seal a seed that has never been opened.
            Error = "Importing a seed needs monero-wallet-rpc so the wallet's address can be " +
                    "confirmed before sealing. Install it (Download & install) and try again.";
            return (false, null);
        }
        catch (Exception ex)
        {
            Error = $"That {what} doesn't appear to be valid: {ex.Message}";
            return (false, null);
        }
    }

    /// <summary>Current daemon tip height, or 0 (full scan) if it can't be reached.</summary>
    private async Task<ulong> GetTipHeightAsync()
    {
        try
        {
            return await MoneroDiagnostics.ProbeDaemonAsync(DaemonAddress.Trim(), await AppServices.Instance.GetNetworkProxyAsync(DaemonAddress.Trim()));
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
