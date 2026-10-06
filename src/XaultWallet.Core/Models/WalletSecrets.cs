namespace XaultWallet.Core.Models;

public enum MoneroNetwork
{
    Mainnet = 0,
    Stagenet = 1,
    Testnet = 2,
}

/// <summary>How a wallet is restored, which also decides what it can do.</summary>
public enum WalletKind
{
    /// <summary>From its 25-word mnemonic seed: full wallet.</summary>
    Seed = 0,

    /// <summary>From its address, private view key and private spend key: full wallet.</summary>
    Keys = 1,

    /// <summary>From its address and private view key only: sees incoming payments, can't spend
    /// (and can't see what it spent, so its balance can read too high).</summary>
    ViewOnly = 2,
}

/// <summary>
/// One wallet: its secrets plus the little metadata that has to survive a lock. Sealed inside an
/// encrypted vault slot as part of a <see cref="WalletProfile"/>. It holds the keys, not the
/// blockchain: the Monero wallet files themselves are never persisted to disk; they are restored
/// into an ephemeral temp directory on unlock and shredded on lock. That is why per-wallet state
/// the wallet file would normally keep (how many subaddresses were handed out, their labels,
/// transaction notes) lives here instead.
///
/// DENIABILITY: this type deliberately has NO notion of "real" vs "decoy". Every slot carries
/// exactly the same kind of data, so a decoy decrypted with the duress password is
/// indistinguishable from the only profile of a single-profile vault.
/// </summary>
public sealed class WalletSecrets
{
    /// <summary>Random, stable identifier within its profile (never shown; carries no meaning).</summary>
    public string Id { get; set; } = NewId();

    /// <summary>What the user calls it. Never logged.</summary>
    public string Name { get; set; } = DefaultName;

    public WalletKind Kind { get; set; } = WalletKind.Seed;

    public MoneroNetwork Network { get; set; } = MoneroNetwork.Stagenet;

    /// <summary>25-word Monero mnemonic seed (<see cref="WalletKind.Seed"/> only).</summary>
    public string Mnemonic { get; set; } = string.Empty;

    /// <summary>Optional seed offset / passphrase used when the seed was generated.</summary>
    public string SeedOffset { get; set; } = string.Empty;

    /// <summary>Primary address (<see cref="WalletKind.Keys"/> and <see cref="WalletKind.ViewOnly"/>).</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Private view key, 64 hex (<see cref="WalletKind.Keys"/> and <see cref="WalletKind.ViewOnly"/>).</summary>
    public string ViewKey { get; set; } = string.Empty;

    /// <summary>Private spend key, 64 hex (<see cref="WalletKind.Keys"/> only).</summary>
    public string SpendKey { get; set; } = string.Empty;

    /// <summary>Block height to restore from, to avoid rescanning the whole chain.</summary>
    public ulong RestoreHeight { get; set; }

    /// <summary>Remote/local daemon this wallet talks to, e.g. "http://127.0.0.1:38081".</summary>
    public string DaemonAddress { get; set; } = "http://127.0.0.1:38081";

    /// <summary>
    /// Randomly generated password that protects the ephemeral monero-wallet-rpc
    /// files while they exist in temp. Regenerated per wallet; never shown to the user.
    /// </summary>
    public string EphemeralWalletPassword { get; set; } = string.Empty;

    /// <summary>
    /// Wipe-on-duress, as an input to <c>VaultManager.Create</c> for the decoy: it becomes the
    /// decoy profile's <see cref="WalletProfile.WipeOtherSlotOnUnlock"/>. Not stored per wallet.
    /// </summary>
    public bool WipeOtherSlotOnUnlock { get; set; }

    /// <summary>Subaddresses handed out per account (account index → how many, index 0 included).
    /// Restored on every open, so a subaddress given to one payer is never given to the next.</summary>
    public Dictionary<uint, uint> SubaddressCounts { get; set; } = new();

    /// <summary>Subaddress labels, keyed "account/index" (e.g. "0/3").</summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>Account names, by account index.</summary>
    public Dictionary<uint, string> AccountLabels { get; set; } = new();

    /// <summary>The user's notes on transactions, keyed by transaction id.</summary>
    public Dictionary<string, string> TxNotes { get; set; } = new();

    /// <summary>Wallets have a name from the start; both slots use the same default, so a default
    /// name never tells a real wallet from a decoy.</summary>
    public const string DefaultName = "My wallet";

    public bool CanSpend => Kind != WalletKind.ViewOnly;

    public static string NewId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}

/// <summary>A saved recipient (address book entry). Kept in the encrypted vault, per profile.</summary>
public sealed class Contact
{
    public string Id { get; set; } = WalletSecrets.NewId();

    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Everything one password opens: its wallets, its address book and which wallet was open last.
/// Every slot of a vault holds one of these — the decoy too, with the same shape — so nothing in
/// the plaintext says which password is the real one.
/// </summary>
public sealed class WalletProfile
{
    public List<WalletSecrets> Wallets { get; set; } = new();

    public List<Contact> Contacts { get; set; } = new();

    /// <summary>The wallet to open first next time (<see cref="WalletSecrets.Id"/>).</summary>
    public string ActiveWalletId { get; set; } = string.Empty;

    /// <summary>
    /// Wipe-on-duress. When true, the first successful use of THIS slot's password (unlock, change
    /// password) overwrites the OTHER slot with random bytes and then clears this flag — so a vault
    /// examined after the wipe looks exactly like a single-profile vault. Only ever set on the decoy;
    /// <c>VaultManager.Create</c> rejects it on the main profile.
    /// </summary>
    public bool WipeOtherSlotOnUnlock { get; set; }

    /// <summary>The wallet to open: the last active one, else the first.</summary>
    public WalletSecrets? ActiveWallet =>
        Wallets.FirstOrDefault(w => w.Id == ActiveWalletId) ?? Wallets.FirstOrDefault();

    /// <summary>A profile holding just <paramref name="wallet"/> (its wipe flag moves to the profile).</summary>
    public static WalletProfile OfOne(WalletSecrets wallet)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        var profile = new WalletProfile
        {
            Wallets = { wallet },
            ActiveWalletId = wallet.Id,
            WipeOtherSlotOnUnlock = wallet.WipeOtherSlotOnUnlock,
        };
        wallet.WipeOtherSlotOnUnlock = false;
        return profile;
    }
}
