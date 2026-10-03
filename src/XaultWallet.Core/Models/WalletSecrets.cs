namespace XaultWallet.Core.Models;

public enum MoneroNetwork
{
    Mainnet = 0,
    Stagenet = 1,
    Testnet = 2,
}

/// <summary>
/// The complete secret payload for one wallet: what gets sealed inside an encrypted vault slot.
/// It is intentionally small (well under the slot size) — it holds the seed, not the blockchain.
/// The Monero wallet files themselves are never persisted to disk; they are restored into an
/// ephemeral temp directory on unlock and shredded on lock.
///
/// DENIABILITY: this type deliberately has NO notion of "real" vs "decoy". Every slot carries
/// exactly the same fields, so a decoy decrypted with the duress password is indistinguishable
/// from the only wallet of a single-wallet vault. (Version 1 of the payload stored a
/// <c>kind</c> and a label — anyone holding the vault file and the duress password could read
/// them and prove a hidden wallet existed. See <c>SlotPayload</c> for the on-disk schema.)
/// </summary>
public sealed class WalletSecrets
{
    public MoneroNetwork Network { get; set; } = MoneroNetwork.Stagenet;

    /// <summary>25-word Monero mnemonic seed (the master secret).</summary>
    public string Mnemonic { get; set; } = string.Empty;

    /// <summary>Optional seed offset / passphrase used when the seed was generated.</summary>
    public string SeedOffset { get; set; } = string.Empty;

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
    /// Wipe-on-duress. When true, the first successful use of THIS slot's password (unlock,
    /// change password, change node) overwrites the OTHER slot with random bytes and then clears
    /// this flag — so a vault examined after the wipe looks exactly like a single-wallet vault.
    /// Only ever set on the decoy; <c>VaultManager.Create</c> rejects it on the main wallet.
    /// </summary>
    public bool WipeOtherSlotOnUnlock { get; set; }
}
