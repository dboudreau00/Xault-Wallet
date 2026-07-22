using XaultWallet.Core.Models;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Lightweight sanity validation for Monero addresses: character set, length, and network
/// prefix. This deliberately does NOT reimplement base58 checksum validation — consistent
/// with this project's rule of never reimplementing Monero cryptography, the final authority
/// on address validity is monero-wallet-rpc, which rejects invalid addresses on transfer.
/// This check exists to catch obvious mistakes (wrong network, truncated paste, typos in
/// length) with a friendly message BEFORE a send is attempted.
/// </summary>
public static class MoneroAddress
{
    // Monero uses the Bitcoin base58 alphabet (no 0, O, I, l).
    private const string Base58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>
    /// Returns null when the address passes sanity checks for the given network, otherwise a
    /// short human-readable reason suitable for showing in the UI.
    /// </summary>
    public static string? Problem(string? address, MoneroNetwork network)
    {
        string a = (address ?? string.Empty).Trim();

        if (a.Length == 0)
        {
            return "Enter a destination address.";
        }

        if (a.Length != 95 && a.Length != 106)
        {
            return $"A Monero address is 95 characters (106 for integrated addresses); this one is {a.Length}.";
        }

        foreach (char c in a)
        {
            if (Base58.IndexOf(c) < 0)
            {
                return $"Address contains an invalid character ('{c}').";
            }
        }

        char p = a[0];
        bool ok = network switch
        {
            MoneroNetwork.Mainnet => p is '4' or '8',
            MoneroNetwork.Testnet => p is '9' or 'A' or 'B',
            MoneroNetwork.Stagenet => p is '5' or '7',
            _ => false,
        };

        if (!ok)
        {
            return $"That doesn't look like a {network} address (starts with '{p}'). Check you're on the right network.";
        }

        return null;
    }
}
