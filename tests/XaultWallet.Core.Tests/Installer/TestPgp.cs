using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using XaultWallet.Core.Installer;

namespace XaultWallet.Core.Tests.Installer;

/// <summary>
/// A minimal OpenPGP clearsigner for tests: RSA, v4, canonical-text signatures — the format
/// binaryFate's hashes.txt uses. Lets tests sign lists with keys they control (to drive the
/// installer end to end, and to prove a valid signature by an UNTRUSTED key is refused). The
/// canonical-text rules are cross-checked against GnuPG by the real-hash-list tests, so a mistake
/// shared by this signer and the verifier would not go unnoticed.
/// </summary>
internal sealed class TestPgp : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public TestPgp()
    {
        RSAParameters p = _rsa.ExportParameters(includePrivateParameters: false);
        Fingerprint = RandomNumberGenerator.GetBytes(20);
        Key = new PgpRsaKey(Fingerprint, p.Modulus!, p.Exponent!);
    }

    public byte[] Fingerprint { get; }

    /// <summary>The public half, as the verifier sees a trusted key.</summary>
    public PgpRsaKey Key { get; }

    /// <summary>A cleartext-signed message over <paramref name="lines"/>.</summary>
    /// <param name="hashHeader">What the "Hash:" armor header claims.</param>
    /// <param name="includeIssuer">Name the signing key in the signature (gpg always does).</param>
    public string ClearSign(IEnumerable<string> lines, string hashHeader = "SHA256", bool includeIssuer = true)
    {
        List<string> text = lines.ToList();
        byte[] canonical = Encoding.UTF8.GetBytes(string.Join("\r\n", text.Select(l => l.TrimEnd(' ', '\t'))));

        // Hashed subpackets: creation time (2), issuer fingerprint (33).
        var hashed = new List<byte>();
        hashed.AddRange([5, 2]);
        hashed.AddRange(BigEndian((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (includeIssuer)
        {
            hashed.AddRange([22, 33, 4]);
            hashed.AddRange(Fingerprint);
        }

        var head = new List<byte> { 4, 0x01, 1, 8 }; // v4, canonical text, RSA, SHA-256
        head.Add((byte)(hashed.Count >> 8));
        head.Add((byte)hashed.Count);
        head.AddRange(hashed);

        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(canonical);
        h.AppendData(head.ToArray());
        h.AppendData([4, 0xFF]);
        h.AppendData(BigEndian((uint)head.Count));
        byte[] digest = h.GetHashAndReset();
        byte[] signature = _rsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var body = new List<byte>(head);
        body.AddRange([0, 0]); // no unhashed subpackets
        body.Add(digest[0]);
        body.Add(digest[1]);
        body.AddRange(Mpi(signature));

        // New-format packet, tag 2, two-octet length.
        int len = body.Count;
        var packet = new List<byte> { 0xC2, (byte)(((len - 192) >> 8) + 192), (byte)((len - 192) & 0xFF) };
        packet.AddRange(body);
        byte[] data = packet.ToArray();

        int crc = OpenPgp.Crc24(data);
        var sb = new StringBuilder();
        sb.Append("-----BEGIN PGP SIGNED MESSAGE-----\n");
        sb.Append("Hash: ").Append(hashHeader).Append("\n\n");
        foreach (string line in text)
        {
            sb.Append(line.StartsWith('-') ? "- " + line : line).Append('\n');
        }

        sb.Append("-----BEGIN PGP SIGNATURE-----\n\n");
        string b64 = Convert.ToBase64String(data);
        for (int i = 0; i < b64.Length; i += 64)
        {
            sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
        }

        sb.Append('=').Append(Convert.ToBase64String([(byte)(crc >> 16), (byte)(crc >> 8), (byte)crc])).Append('\n');
        sb.Append("-----END PGP SIGNATURE-----\n");
        return sb.ToString();
    }

    /// <summary>A detached signature (".asc") over <paramref name="data"/>, the way Tor Project signs
    /// its checksum lists: binary signature type, SHA-512.</summary>
    /// <param name="signatureType">0x00 (binary) normally; 0x01 to make a wrong-type signature.</param>
    public string DetachedSign(byte[] data, byte signatureType = 0x00)
    {
        var hashed = new List<byte>();
        hashed.AddRange([5, 2]);
        hashed.AddRange(BigEndian((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        hashed.AddRange([22, 33, 4]);
        hashed.AddRange(Fingerprint);

        var head = new List<byte> { 4, signatureType, 1, 10 }; // v4, type, RSA, SHA-512
        head.Add((byte)(hashed.Count >> 8));
        head.Add((byte)hashed.Count);
        head.AddRange(hashed);

        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        h.AppendData(data);
        h.AppendData(head.ToArray());
        h.AppendData([4, 0xFF]);
        h.AppendData(BigEndian((uint)head.Count));
        byte[] digest = h.GetHashAndReset();
        byte[] signature = _rsa.SignHash(digest, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);

        var body = new List<byte>(head);
        body.AddRange([0, 0]);
        body.Add(digest[0]);
        body.Add(digest[1]);
        body.AddRange(Mpi(signature));

        int len = body.Count;
        var packet = new List<byte> { 0xC2, (byte)(((len - 192) >> 8) + 192), (byte)((len - 192) & 0xFF) };
        packet.AddRange(body);
        byte[] bytes = packet.ToArray();

        int crc = OpenPgp.Crc24(bytes);
        var sb = new StringBuilder();
        sb.Append("-----BEGIN PGP SIGNATURE-----\n\n");
        string b64 = Convert.ToBase64String(bytes);
        for (int i = 0; i < b64.Length; i += 64)
        {
            sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
        }

        sb.Append('=').Append(Convert.ToBase64String([(byte)(crc >> 16), (byte)(crc >> 8), (byte)crc])).Append('\n');
        sb.Append("-----END PGP SIGNATURE-----\n");
        return sb.ToString();
    }

    private static byte[] BigEndian(uint v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static IEnumerable<byte> Mpi(byte[] value)
    {
        int skip = 0;
        while (skip < value.Length - 1 && value[skip] == 0)
        {
            skip++;
        }

        ReadOnlySpan<byte> v = value.AsSpan(skip);
        int bits = (v.Length * 8) - System.Numerics.BitOperations.LeadingZeroCount((uint)v[0]) + 24;
        return new byte[] { (byte)(bits >> 8), (byte)bits }.Concat(v.ToArray());
    }

    public void Dispose() => _rsa.Dispose();
}
