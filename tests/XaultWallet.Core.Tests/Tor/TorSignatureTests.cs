using System.Text;
using XaultWallet.Core.Installer;
using XaultWallet.Core.Tests.Installer;
using XaultWallet.Core.Tor;
using Xunit;

namespace XaultWallet.Core.Tests.Tor;

/// <summary>
/// Detached signatures, as Tor Project publishes them, held to real data: the 15.0.24 checksum list
/// and its .asc must verify with the key the app pins, and nothing else may.
/// </summary>
public sealed class TorSignatureTests
{
    // A Windows checkout may turn the raw strings' line endings into CRLF; the published bytes are LF.
    private static byte[] RealSums => Encoding.UTF8.GetBytes(RealTorChecksums.Sums.Replace("\r\n", "\n", StringComparison.Ordinal));

    private static string RealSignature => RealTorChecksums.Signature.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void The_Bundled_Key_Is_The_Pinned_Tor_Browser_Signing_Subkey()
    {
        PgpRsaKey key = Assert.Single(TorSigningKeys.Trusted);
        Assert.Equal(TorSigningKeys.TorBrowserSigningSubkey, key.Fingerprint);
        Assert.Equal("022DA248432D2A0E0F54E65E316C1FACD62D07D9", key.Fingerprint);
    }

    [Fact]
    public void The_Real_Checksum_List_Verifies_With_The_Pinned_Key()
    {
        OpenPgp.VerifyDetached(RealSums, RealSignature, TorSigningKeys.Trusted, "Tor Project's checksum list", TorSigningKeys.SignerName);

        TorBundleArchive bundle = TorReleaseList.Select(Encoding.UTF8.GetString(RealSums).Split('\n'), "windows-x86_64", RealTorChecksums.Version);
        Assert.Equal(RealTorChecksums.ExpectedWindowsSha256, bundle.Sha256);
        Assert.Equal("tor-expert-bundle-windows-x86_64-15.0.24.tar.gz", bundle.FileName);
    }

    [Fact]
    public void One_Changed_Checksum_Breaks_The_Signature()
    {
        string tampered = Encoding.UTF8.GetString(RealSums).Replace(RealTorChecksums.ExpectedWindowsSha256, new string('0', 64), StringComparison.Ordinal);

        var ex = Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyDetached(
            Encoding.UTF8.GetBytes(tampered), RealSignature, TorSigningKeys.Trusted, "Tor Project's checksum list", TorSigningKeys.SignerName));
        Assert.Contains("no valid signature by the Tor Browser Developers signing key", ex.Message);
    }

    [Fact]
    public void Monero_Release_Key_Does_Not_Vouch_For_Tor_Downloads()
    {
        Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyDetached(
            RealSums, RealSignature, MoneroSigningKeys.Trusted, "Tor Project's checksum list", TorSigningKeys.SignerName));
    }

    [Fact]
    public void A_Detached_Signature_By_A_Trusted_Test_Key_Verifies_And_An_Untrusted_One_Does_Not()
    {
        using var trusted = new TestPgp();
        using var stranger = new TestPgp();
        byte[] data = Encoding.UTF8.GetBytes("abc  tor-expert-bundle-linux-x86_64-15.0.30.tar.gz\n");

        OpenPgp.VerifyDetached(data, trusted.DetachedSign(data), [trusted.Key], "List", "the test key");
        Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyDetached(data, stranger.DetachedSign(data), [trusted.Key], "List", "the test key"));
    }

    [Fact]
    public void A_Text_Signature_Is_Not_Accepted_As_A_File_Signature()
    {
        using var signer = new TestPgp();
        byte[] data = "payload"u8.ToArray();

        var ex = Assert.Throws<SignatureCheckException>(() =>
            OpenPgp.VerifyDetached(data, signer.DetachedSign(data, signatureType: 0x01), [signer.Key], "List", "the test key"));
        Assert.Contains("not a signature over a file", ex.Message);
    }

    [Fact]
    public void Text_After_The_Signature_Is_Refused()
    {
        using var signer = new TestPgp();
        byte[] data = "payload"u8.ToArray();
        string signature = signer.DetachedSign(data) + "\nsha256  something-else.tar.gz\n";

        var ex = Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyDetached(data, signature, [signer.Key], "List", "the test key"));
        Assert.Contains("text after the signature", ex.Message);
    }

    [Fact]
    public void A_Cleartext_Message_Is_Not_A_Detached_Signature()
    {
        using var signer = new TestPgp();
        string clear = signer.ClearSign(["payload"]);

        Assert.Throws<SignatureCheckException>(() => OpenPgp.VerifyDetached("payload"u8, clear, [signer.Key], "List", "the test key"));
    }
}
