using XaultWallet.Core.Installer;

namespace XaultWallet.Core.Tor;

/// <summary>
/// The key trusted to sign Tor Project's checksum lists: the Tor Browser Developers signing key
/// (primary EF6E 286D DA85 EA2A 4BA7 DE68 4E2C 6E87 9329 8290), which also signs every Tor Expert
/// Bundle. The key material ships inside the app (torbrowser.asc, exported unchanged from Tor
/// Project's own WKD, openpgpkey.torproject.org); the signing subkey below is pinned in code and
/// the bundled file is only accepted if it holds exactly that key.
///
/// Only the current signing subkey is trusted, not the primary key (certification only) or older
/// subkeys. When Tor Project rotates to a new subkey (this one expires 2028-11-28), installs fail
/// with "no valid signature" until an app update pins the new one: refusing is the safe failure.
///
/// Nothing is fetched to decide trust: a key served alongside a download proves nothing.
/// </summary>
public static class TorSigningKeys
{
    /// <summary>The Tor Browser Developers primary key (RSA 4096, certification only).</summary>
    public const string TorBrowserPrimary = "EF6E286DDA85EA2A4BA7DE684E2C6E8793298290";

    /// <summary>Its signing subkey (RSA 4096, created 2026-08-11, expires 2028-11-28).</summary>
    public const string TorBrowserSigningSubkey = "022DA248432D2A0E0F54E65E316C1FACD62D07D9";

    /// <summary>Whose signature the installer requires, for messages.</summary>
    public const string SignerName = "the Tor Browser Developers signing key";

    private const string ResourceName = "XaultWallet.Core.Tor.torbrowser.asc";

    private static readonly Lazy<IReadOnlyList<PgpRsaKey>> s_trusted = new(() => Load());

    /// <summary>The pinned key, read from the bundled key file.</summary>
    /// <exception cref="InvalidOperationException">The bundled file doesn't hold the pinned key.</exception>
    public static IReadOnlyList<PgpRsaKey> Trusted => s_trusted.Value;

    private static List<PgpRsaKey> Load()
    {
        using Stream stream = typeof(TorSigningKeys).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Tor Project signing key is missing from this build.");
        using var reader = new StreamReader(stream);
        IReadOnlyList<PgpRsaKey> keys = OpenPgp.ReadRsaKeys(reader.ReadToEnd());

        // The block must really be Tor Browser's key (its primary is there), and the trusted subkey
        // must appear exactly once.
        if (!keys.Any(k => k.Fingerprint == TorBrowserPrimary))
        {
            throw new InvalidOperationException("The bundled Tor Project signing key doesn't match its pinned fingerprint.");
        }

        List<PgpRsaKey> pinned = keys.Where(k => k.Fingerprint == TorBrowserSigningSubkey).ToList();
        if (pinned.Count != 1)
        {
            throw new InvalidOperationException("The bundled Tor Project signing key doesn't match its pinned fingerprint.");
        }

        return pinned;
    }
}
