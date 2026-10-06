using XaultWallet.Core.Installer;
using Xunit;

namespace XaultWallet.Core.Tests.Installer;

/// <summary>
/// The "install monero-wallet-rpc for me" button trusts a download only through binaryFate's
/// signature on Monero's hash list. These tests pin that chain of trust: the bundled key is the
/// published one, a genuine list verifies, and every kind of tampering is refused.
/// </summary>
public class SignedReleaseListTests
{
    [Fact]
    public void Bundled_Key_Is_BinaryFates_Primary_And_Signing_Subkey()
    {
        IReadOnlyList<PgpRsaKey> keys = MoneroSigningKeys.Trusted;

        Assert.Equal(2, keys.Count);
        Assert.Contains(keys, k => k.Fingerprint == "81AC591FE9C4B65C5806AFC3F0AF4D462A0BDF92");
        Assert.Contains(keys, k => k.Fingerprint == "AD564CDA8F1665ACE78B5DFD2593838EABB1F655");
        Assert.All(keys, k => Assert.Equal(4096, k.ModulusBits));
    }

    [Fact]
    public void Genuine_Hash_List_Verifies_And_Names_Each_Platforms_Archive()
    {
        IReadOnlyList<string> signed = OpenPgp.VerifyClearSigned(RealHashList.V0_18_5_1, MoneroSigningKeys.Trusted);

        Assert.Equal("# This GPG-signed message exists to confirm the SHA256 sums of Monero binaries.", signed[0]);
        Assert.Equal("# ~binaryFate", signed[^1]);

        MoneroCliArchive win = MoneroReleaseList.Select(signed, "win-x64");
        Assert.Equal("monero-win-x64-v0.18.5.1.zip", win.FileName);
        Assert.Equal("0.18.5.1", win.Version);
        Assert.Equal("cf2ae8273977697d9ef2031c7337b781e6e5936578f602444b2990a173a2437d", win.Sha256);

        Assert.Equal("22a7dda7b0cb699fdd6b7674c3b4a4465b337cc98a54983523b759e1e7cc9958",
            MoneroReleaseList.Select(signed, "linux-x64").Sha256);
        Assert.Equal("dba08921841e675384ce019fd7c93b59fe7b1e6edaa0a3cf0e3253e263f61864",
            MoneroReleaseList.Select(signed, "mac-armv8").Sha256);
        Assert.Equal("monero-linux-armv8-v0.18.5.1.tar.bz2", MoneroReleaseList.Select(signed, "linux-armv8").FileName);
    }

    [Fact]
    public void Gui_And_Source_Entries_Are_Not_Command_Line_Builds()
    {
        IReadOnlyList<string> signed = OpenPgp.VerifyClearSigned(RealHashList.V0_18_5_1, MoneroSigningKeys.Trusted);
        IReadOnlyList<MoneroCliArchive> all = MoneroReleaseList.Parse(signed);

        Assert.Equal(12, all.Count); // the 12 CLI builds; the source tarball, 6 GUI lines and comments are ignored
        Assert.DoesNotContain(all, a => a.FileName.Contains("gui", StringComparison.Ordinal));
        Assert.DoesNotContain(all, a => a.FileName.Contains("source", StringComparison.Ordinal));
        Assert.All(all, a => Assert.Equal("0.18.5.1", a.Version));
    }

    [Fact]
    public void Windows_Line_Endings_Still_Verify()
    {
        string crlf = RealHashList.V0_18_5_1.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.NotEmpty(OpenPgp.VerifyClearSigned(crlf, MoneroSigningKeys.Trusted));
    }

    [Fact]
    public void Trailing_Whitespace_Is_Not_Part_Of_The_Signed_Text()
    {
        // RFC 4880 §7.1: trailing spaces/tabs are stripped before hashing, as gpg does.
        string padded = RealHashList.V0_18_5_1.Replace("#\n## CLI", "#  \t\n## CLI");
        Assert.NotEqual(RealHashList.V0_18_5_1, padded);
        Assert.NotEmpty(OpenPgp.VerifyClearSigned(padded, MoneroSigningKeys.Trusted));
    }

    public static TheoryData<string, string> Tampering => new()
    {
        // A swapped checksum: the attack the signature exists to stop.
        { "cf2ae8273977697d9ef2031c7337b781e6e5936578f602444b2990a173a2437d  monero-win-x64",
          "0000000000000000000000000000000000000000000000000000000000000000  monero-win-x64" },
        // A renamed archive with the original checksum.
        { "monero-win-x64-v0.18.5.1.zip", "monero-win-x64-v0.18.5.9.zip" },
        // A line added to the signed text.
        { "## CLI\n", "## CLI\n0123456789012345678901234567890123456789012345678901234567890123  monero-win-x64-v9.9.9.9.zip\n" },
        // A line removed from it.
        { "# ~binaryFate\n", "" },
        // A different hash declared than the signature uses.
        { "Hash: SHA256", "Hash: SHA512" },
        // The signature itself damaged (the armor checksum catches it).
        { "iQIzBAEBCAAdFiEEgaxZH+nEtlxYBq/D8K9NRioL35IFAmpefA4ACgkQ8K9NRioL", "iQIzBAEBCAAdFiEEgaxZH+nEtlxYBq/D8K9NRioL35IFAmpefA4ACgkQ8K9NRioM" },
        // Text after the signature: not covered by it, and a careless parser would read it.
        { "-----END PGP SIGNATURE-----", "-----END PGP SIGNATURE-----\n0000000000000000000000000000000000000000000000000000000000000000  monero-win-x64-v0.18.5.1.zip" },
        // Text before the signed block.
        { "-----BEGIN PGP SIGNED MESSAGE-----", "0000000000000000000000000000000000000000000000000000000000000000  monero-win-x64-v0.18.5.1.zip\n-----BEGIN PGP SIGNED MESSAGE-----" },
        // An unknown armor header (only Hash: is defined for signed messages).
        { "Hash: SHA256\n", "Hash: SHA256\nComment: trust me\n" },
        // The signature removed.
        { "-----BEGIN PGP SIGNATURE-----", "-----BEGIN PGP SIGNATURE----- (removed)" },
    };

    [Theory]
    [MemberData(nameof(Tampering))]
    public void Tampered_Hash_List_Is_Refused(string original, string replacement)
    {
        string text = RealHashList.V0_18_5_1.Replace("\r\n", "\n");
        Assert.Contains(original, text);
        string tampered = text.Replace(original, replacement);

        Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyClearSigned(tampered, MoneroSigningKeys.Trusted));
    }

    [Fact]
    public void Genuine_List_Is_Refused_When_Its_Signer_Is_Not_Trusted()
    {
        using var stranger = new TestPgp();
        Assert.Throws<SignatureCheckException>(
            () => OpenPgp.VerifyClearSigned(RealHashList.V0_18_5_1, [stranger.Key]));
    }

    [Fact]
    public void A_Valid_Signature_By_Any_Other_Key_Is_Refused()
    {
        // An attacker can produce a perfectly valid signature with their own key: it must not count.
        using var attacker = new TestPgp();
        string forged = attacker.ClearSign(["0000000000000000000000000000000000000000000000000000000000000000  monero-win-x64-v0.18.5.1.zip"]);

        Assert.NotEmpty(OpenPgp.VerifyClearSigned(forged, [attacker.Key])); // valid in itself…
        Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyClearSigned(forged, MoneroSigningKeys.Trusted)); // …but not binaryFate's
    }

    [Fact]
    public void Signature_Without_An_Issuer_Is_Checked_Against_Every_Trusted_Key()
    {
        using var signer = new TestPgp();
        using var other = new TestPgp();
        string signed = signer.ClearSign(["hello"], includeIssuer: false);

        Assert.Equal(new[] { "hello" }, OpenPgp.VerifyClearSigned(signed, [other.Key, signer.Key]));
        Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyClearSigned(signed, [other.Key]));
    }

    [Fact]
    public void Dash_Escaped_Lines_Come_Back_Unescaped()
    {
        using var signer = new TestPgp();
        string signed = signer.ClearSign(["-----BEGIN PGP SIGNATURE-----", "- not a list item", "plain"]);

        Assert.Contains("- -----BEGIN PGP SIGNATURE-----", signed);
        Assert.Equal(new[] { "-----BEGIN PGP SIGNATURE-----", "- not a list item", "plain" },
            OpenPgp.VerifyClearSigned(signed, [signer.Key]));
    }

    [Fact]
    public void Weak_Hash_Header_Is_Refused_Even_If_It_Matches()
    {
        using var signer = new TestPgp();
        // The signer always signs with SHA-256; a list that only declares SHA1 is refused outright.
        string signed = signer.ClearSign(["x"], hashHeader: "SHA1");
        Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyClearSigned(signed, [signer.Key]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("-----BEGIN PGP SIGNED MESSAGE-----")]
    [InlineData("-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\ntext\n-----BEGIN PGP SIGNATURE-----\n\n!!!!\n-----END PGP SIGNATURE-----")]
    [InlineData("-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\ntext\n-----BEGIN PGP SIGNATURE-----\n\nAAAA\n")]
    [InlineData("-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\n-not escaped\n-----BEGIN PGP SIGNATURE-----\n\nwsBcBAEBCAAQBQJm\n-----END PGP SIGNATURE-----")]
    public void Malformed_Input_Is_Refused_Not_Crashed_On(string input) =>
        Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyClearSigned(input, MoneroSigningKeys.Trusted));

    [Theory]
    [InlineData("Windows", "X64", "win-x64")]
    [InlineData("Windows", "Arm64", "win-x64")]
    [InlineData("Linux", "X64", "linux-x64")]
    [InlineData("Linux", "Arm64", "linux-armv8")]
    [InlineData("OSX", "Arm64", "mac-armv8")]
    [InlineData("OSX", "X64", "mac-x64")]
    [InlineData("OSX", "Arm", null)]
    public void Each_System_Maps_To_Moneros_Platform_Name(string os, string arch, string? expected)
    {
        var platform = System.Runtime.InteropServices.OSPlatform.Create(os.ToUpperInvariant());
        var architecture = Enum.Parse<System.Runtime.InteropServices.Architecture>(arch);
        Assert.Equal(expected, MoneroReleaseList.PlatformName(platform, architecture));
    }

    [Fact]
    public void Windows_Builds_Ship_An_Exe()
    {
        Assert.Equal("monero-wallet-rpc.exe", MoneroReleaseList.WalletRpcFileName("win-x64"));
        Assert.Equal("monero-wallet-rpc", MoneroReleaseList.WalletRpcFileName("linux-x64"));
        Assert.Equal("monero-wallet-rpc", MoneroReleaseList.WalletRpcFileName("mac-armv8"));
    }
}
