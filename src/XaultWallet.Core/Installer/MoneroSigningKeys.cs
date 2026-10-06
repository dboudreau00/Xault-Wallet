namespace XaultWallet.Core.Installer;

/// <summary>
/// The keys trusted to sign Monero's release hash list: binaryFate's, the Monero release
/// maintainer. The key material ships inside the app (binaryfate.asc, copied unchanged from the
/// Monero source tree, utils/gpg_keys/binaryfate.asc); the fingerprints below are pinned in code
/// and the bundled file is only accepted if its keys hash to exactly these. They are the same
/// fingerprints getmonero.org and the Monero source repository publish.
///
/// Nothing is fetched to decide trust: a key served alongside a download proves nothing.
/// </summary>
public static class MoneroSigningKeys
{
    /// <summary>binaryFate's primary key (RSA 4096, created 2019-12-12, no expiry).</summary>
    public const string BinaryFatePrimary = "81AC591FE9C4B65C5806AFC3F0AF4D462A0BDF92";

    /// <summary>binaryFate's signing-capable subkey (RSA 4096, bound to the primary key).</summary>
    public const string BinaryFateSubkey = "AD564CDA8F1665ACE78B5DFD2593838EABB1F655";

    private const string ResourceName = "XaultWallet.Core.Installer.binaryfate.asc";

    private static readonly Lazy<IReadOnlyList<PgpRsaKey>> s_trusted = new(() => Load());

    /// <summary>The pinned keys, read from the bundled key file.</summary>
    /// <exception cref="InvalidOperationException">The bundled file doesn't hold exactly the pinned keys.</exception>
    public static IReadOnlyList<PgpRsaKey> Trusted => s_trusted.Value;

    private static List<PgpRsaKey> Load()
    {
        using Stream stream = typeof(MoneroSigningKeys).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Monero release signing key is missing from this build.");
        using var reader = new StreamReader(stream);
        IReadOnlyList<PgpRsaKey> keys = OpenPgp.ReadRsaKeys(reader.ReadToEnd());

        List<PgpRsaKey> pinned = keys.Where(k => k.Fingerprint is BinaryFatePrimary or BinaryFateSubkey).ToList();
        if (pinned.Count != 2 || pinned[0].Fingerprint == pinned[1].Fingerprint)
        {
            throw new InvalidOperationException("The bundled Monero release signing key doesn't match its pinned fingerprints.");
        }

        return pinned;
    }
}
