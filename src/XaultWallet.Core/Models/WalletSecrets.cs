using System.Text.Json.Serialization;

namespace XaultWallet.Core.Models;

public enum MoneroNetwork
{
    Mainnet = 0,
    Stagenet = 1,
    Testnet = 2,
}

/// <summary>
/// Whether a profile is the user's real wallet or the decoy that unlocks under
/// the duress password. This value lives ONLY inside the encrypted slot, so an
/// attacker who has not decrypted the slot cannot tell which is which.
/// </summary>
public enum ProfileKind
{
    Real = 0,
    Duress = 1,
}

/// <summary>
/// The complete secret payload for one wallet. This is what gets serialised to
/// JSON, padded, and sealed inside an encrypted vault slot. It is intentionally
/// small (well under the slot size) — it holds the seed, not the blockchain.
/// The Monero wallet files themselves are never persisted to disk; they are
/// restored into an ephemeral temp directory on unlock and shredded on lock.
/// </summary>
public sealed class WalletSecrets
{
    [JsonPropertyName("kind")]
    public ProfileKind Kind { get; set; } = ProfileKind.Real;

    [JsonPropertyName("label")]
    public string Label { get; set; } = "Main";

    [JsonPropertyName("network")]
    public MoneroNetwork Network { get; set; } = MoneroNetwork.Mainnet;

    /// <summary>25-word Monero mnemonic seed (the master secret).</summary>
    [JsonPropertyName("mnemonic")]
    public string Mnemonic { get; set; } = string.Empty;

    /// <summary>Optional seed offset / passphrase used when the seed was generated.</summary>
    [JsonPropertyName("seedOffset")]
    public string SeedOffset { get; set; } = string.Empty;

    /// <summary>Block height to restore from, to avoid rescanning the whole chain.</summary>
    [JsonPropertyName("restoreHeight")]
    public ulong RestoreHeight { get; set; }

    /// <summary>Remote/local daemon this wallet talks to, e.g. "http://127.0.0.1:18081".</summary>
    [JsonPropertyName("daemonAddress")]
    public string DaemonAddress { get; set; } = "http://127.0.0.1:18081";

    /// <summary>
    /// Randomly generated password that protects the ephemeral monero-wallet-rpc
    /// files while they exist in temp. Regenerated per wallet; never shown to the user.
    /// </summary>
    [JsonPropertyName("ephemeralWalletPassword")]
    public string EphemeralWalletPassword { get; set; } = string.Empty;

    /// <summary>
    /// Only meaningful on a Duress profile. When true, opening this decoy also wipes
    /// the real slot from the device. Stored INSIDE the encrypted payload so the
    /// existence of a wipe policy (and therefore of a hidden wallet) never leaks.
    /// </summary>
    [JsonPropertyName("duressWipeReal")]
    public bool DuressWipeReal { get; set; }
}
