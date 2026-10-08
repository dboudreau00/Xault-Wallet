using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace XaultWallet.Core.Installer;

/// <summary>The download could not be proven authentic. The message is safe to show.</summary>
public sealed class SignatureCheckException(string message) : Exception(message);

/// <summary>
/// An RSA public key from an OpenPGP key block (a primary key or a subkey), identified by its
/// version-4 fingerprint.
/// </summary>
public sealed class PgpRsaKey
{
    private readonly byte[] _fingerprint;
    private readonly byte[] _modulus;
    private readonly byte[] _exponent;

    internal PgpRsaKey(byte[] fingerprint, byte[] modulus, byte[] exponent)
    {
        _fingerprint = fingerprint;
        _modulus = modulus;
        _exponent = exponent;
    }

    /// <summary>The 40-hex-digit v4 fingerprint: upper case, no spaces.</summary>
    public string Fingerprint => Convert.ToHexString(_fingerprint);

    internal ReadOnlySpan<byte> FingerprintBytes => _fingerprint;

    /// <summary>The low 64 bits of the fingerprint: what an "issuer" subpacket names.</summary>
    internal ReadOnlySpan<byte> KeyId => _fingerprint.AsSpan(_fingerprint.Length - 8);

    internal int ModulusBits => (_modulus.Length * 8) - BitOperations.LeadingZeroCount((uint)_modulus[0]) + 24;

    /// <summary>RSASSA-PKCS1-v1_5 over <paramref name="digest"/>, as OpenPGP uses it (RFC 4880 §5.2.2).</summary>
    internal bool Verify(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> signature, HashAlgorithmName hash)
    {
        // An MPI drops leading zero bytes; PKCS#1 wants the signature at exactly the modulus length.
        int size = _modulus.Length;
        if (signature.Length > size)
        {
            return false;
        }

        byte[] padded = new byte[size];
        signature.CopyTo(padded.AsSpan(size - signature.Length));

        using RSA rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = _modulus, Exponent = _exponent });
        return rsa.VerifyHash(digest, padded, hash, RSASignaturePadding.Pkcs1);
    }
}

/// <summary>
/// The small part of OpenPGP (RFC 4880) needed to check a release-hash list the way
/// <c>gpg --verify</c> does: read RSA public keys from an armored key block, and verify either a
/// cleartext-signed message ("-----BEGIN PGP SIGNED MESSAGE-----", Monero's hashes.txt) or a
/// detached signature over a file (an ".asc" next to it, Tor Project's checksum lists).
///
/// Deliberately narrow, and strict where it can be: version-4 RSA keys and signatures, SHA-256 or
/// SHA-512, and only the one signature type each format uses (canonical text for cleartext, binary
/// for detached). Anything else — another algorithm, a weak hash, a critical feature it doesn't
/// know, text outside the signed block — is refused, so a failure here always means "not proven",
/// never "accepted by a lenient guess". What it returns or accepts is exactly what the signature
/// covers: callers parse nothing else.
/// </summary>
internal static class OpenPgp
{
    private const int TagSignature = 2;
    private const int TagPublicKey = 6;
    private const int TagPublicSubkey = 14;

    private const byte AlgoRsa = 1;
    private const byte AlgoRsaSignOnly = 3;
    private const byte SigTypeBinary = 0x00;
    private const byte SigTypeCanonicalText = 0x01;

    /// <summary>A format problem found while parsing; the public entry points turn it into a
    /// <see cref="SignatureCheckException"/> that names what was being checked.</summary>
    private sealed class FormatProblem(string why) : Exception(why);

    /// <summary>Every v4 RSA key (primary and subkeys) in an ASCII-armored public key block.</summary>
    public static IReadOnlyList<PgpRsaKey> ReadRsaKeys(string armoredKeyBlock)
    {
        try
        {
            return ReadRsaKeysCore(armoredKeyBlock);
        }
        catch (FormatProblem p)
        {
            throw new SignatureCheckException($"The signing key could not be read: {p.Message}.");
        }
    }

    private static List<PgpRsaKey> ReadRsaKeysCore(string armoredKeyBlock)
    {
        ArgumentNullException.ThrowIfNull(armoredKeyBlock);
        string[] lines = SplitLines(armoredKeyBlock);
        int i = SkipBlank(lines, 0);
        byte[] data = DecodeArmor(lines, ref i, "PUBLIC KEY BLOCK");

        var keys = new List<PgpRsaKey>();
        foreach ((int tag, byte[] body) in ReadPackets(data))
        {
            if (tag is TagPublicKey or TagPublicSubkey && ReadRsaKey(body) is { } key)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>
    /// Verify a cleartext-signed message against <paramref name="trustedKeys"/> and return the signed
    /// lines (dash-escaping removed, trailing whitespace stripped — exactly what was signed).
    /// </summary>
    /// <exception cref="SignatureCheckException">The message is malformed, or no signature in it
    /// verifies with a trusted key.</exception>
    public static IReadOnlyList<string> VerifyClearSigned(string message, IReadOnlyList<PgpRsaKey> trustedKeys)
    {
        try
        {
            return VerifyClearSignedCore(message, trustedKeys);
        }
        catch (FormatProblem p)
        {
            throw new SignatureCheckException($"The signed release list could not be checked: {p.Message}.");
        }
    }

    /// <summary>
    /// Verify a detached signature (<paramref name="armoredSignature"/>, the content of an ".asc"
    /// file) over <paramref name="data"/>, byte for byte, against <paramref name="trustedKeys"/>.
    /// </summary>
    /// <param name="subject">What is being checked, for messages ("Tor Project's checksum list").</param>
    /// <param name="signerName">Whose key it must be, for messages ("the Tor Browser signing key").</param>
    /// <exception cref="SignatureCheckException">The signature is malformed, or none of its
    /// signatures verifies with a trusted key.</exception>
    public static void VerifyDetached(ReadOnlySpan<byte> data, string armoredSignature, IReadOnlyList<PgpRsaKey> trustedKeys,
        string subject, string signerName)
    {
        ArgumentNullException.ThrowIfNull(armoredSignature);
        ArgumentNullException.ThrowIfNull(trustedKeys);

        bool verified = false;
        try
        {
            string[] lines = SplitLines(armoredSignature);
            int i = SkipBlank(lines, 0);
            byte[] signatureData = DecodeArmor(lines, ref i, "SIGNATURE");
            if (SkipBlank(lines, i) != lines.Length)
            {
                throw Fail("there is text after the signature");
            }

            foreach ((int tag, byte[] body) in ReadPackets(signatureData))
            {
                if (tag != TagSignature)
                {
                    throw Fail("the signature block holds something other than a signature");
                }

                verified |= VerifySignaturePacket(body, data, SigTypeBinary, declaredHashes: null, trustedKeys);
            }
        }
        catch (FormatProblem p)
        {
            throw new SignatureCheckException($"{subject} could not be checked: {p.Message}.");
        }

        if (!verified)
        {
            throw new SignatureCheckException($"{subject} has no valid signature by {signerName}.");
        }
    }

    private static List<string> VerifyClearSignedCore(string message, IReadOnlyList<PgpRsaKey> trustedKeys)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(trustedKeys);

        string[] lines = SplitLines(message);
        int i = SkipBlank(lines, 0);
        Expect(lines, i++, "-----BEGIN PGP SIGNED MESSAGE-----");

        // Armor headers. "Hash:" is the only one defined for a signed message (RFC 4880 §7).
        var declaredHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (; i < lines.Length && lines[i].Length > 0; i++)
        {
            const string HashHeader = "Hash: ";
            if (!lines[i].StartsWith(HashHeader, StringComparison.Ordinal))
            {
                throw Fail("it has an unexpected header");
            }

            foreach (string name in lines[i][HashHeader.Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                declaredHashes.Add(name);
            }
        }

        if (i >= lines.Length)
        {
            throw Fail("it has no signed text");
        }

        i++; // the blank line that ends the headers

        // The signed text, dash-unescaped (RFC 4880 §7.1). Trailing spaces and tabs are not part
        // of what is signed.
        var text = new List<string>();
        for (; ; i++)
        {
            if (i >= lines.Length)
            {
                throw Fail("the signature is missing");
            }

            string line = lines[i];
            if (line == "-----BEGIN PGP SIGNATURE-----")
            {
                break;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                line = line[2..];
            }
            else if (line.StartsWith('-'))
            {
                throw Fail("the signed text is malformed");
            }

            text.Add(line.TrimEnd(' ', '\t'));
        }

        byte[] signatureData = DecodeArmor(lines, ref i, "SIGNATURE");
        if (SkipBlank(lines, i) != lines.Length)
        {
            // Text after the signature is not covered by it; a caller that read the whole file
            // could be fooled by it. Refuse rather than ignore.
            throw Fail("there is text after the signature");
        }

        // Canonical text: lines joined by CRLF, with no line ending after the last line.
        byte[] canonical = Encoding.UTF8.GetBytes(string.Join("\r\n", text));

        bool verified = false;
        foreach ((int tag, byte[] body) in ReadPackets(signatureData))
        {
            if (tag != TagSignature)
            {
                throw Fail("the signature block holds something other than a signature");
            }

            verified |= VerifySignaturePacket(body, canonical, SigTypeCanonicalText, declaredHashes, trustedKeys);
        }

        if (!verified)
        {
            throw new SignatureCheckException(
                "The release list's signature is not valid, or was not made by Monero's release signing key.");
        }

        return text;
    }

    /// <summary>True when this v4 signature packet is a valid signature of type
    /// <paramref name="expectedSigType"/> over <paramref name="signedData"/> by one of
    /// <paramref name="trustedKeys"/>. <paramref name="declaredHashes"/> is the cleartext format's
    /// "Hash:" header (null for a detached signature, which has none).</summary>
    private static bool VerifySignaturePacket(
        byte[] p, ReadOnlySpan<byte> signedData, byte expectedSigType, HashSet<string>? declaredHashes, IReadOnlyList<PgpRsaKey> trustedKeys)
    {
        if (p.Length < 6 || p[0] != 4)
        {
            throw Fail("its signature format is not supported");
        }

        byte sigType = p[1];
        byte pubAlgo = p[2];
        byte hashAlgo = p[3];
        if (sigType != expectedSigType)
        {
            throw Fail(expectedSigType == SigTypeCanonicalText ? "it is not a signature over text" : "it is not a signature over a file");
        }

        (HashAlgorithmName hash, string hashName) = hashAlgo switch
        {
            8 => (HashAlgorithmName.SHA256, "SHA256"),
            10 => (HashAlgorithmName.SHA512, "SHA512"),
            _ => throw Fail("it uses a hash algorithm this app does not accept"),
        };

        if (declaredHashes is not null && !declaredHashes.Contains(hashName))
        {
            throw Fail("its Hash header doesn't match the signature");
        }

        if (pubAlgo is not (AlgoRsa or AlgoRsaSignOnly))
        {
            return false; // not an RSA signature: no trusted key could have made it
        }

        int hashedLength = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4));
        int o = 6;
        if (hashedLength > p.Length - o - 2)
        {
            throw Fail("its signature is truncated");
        }

        ReadOnlySpan<byte> hashedArea = p.AsSpan(o, hashedLength);
        o += hashedLength;
        int unhashedLength = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(o));
        o += 2;
        if (unhashedLength > p.Length - o - 2)
        {
            throw Fail("its signature is truncated");
        }

        ReadOnlySpan<byte> unhashedArea = p.AsSpan(o, unhashedLength);
        o += unhashedLength;
        byte left0 = p[o];
        byte left1 = p[o + 1];
        o += 2;
        byte[] signature = ReadMpi(p, ref o);
        if (o != p.Length)
        {
            throw Fail("its signature has unexpected trailing data");
        }

        // Which key made it. The unhashed area is not covered by the signature, but it only picks
        // which trusted key to try: the RSA check below still has to pass.
        byte[]? issuerFingerprint = null;
        byte[]? issuerKeyId = null;
        ReadSubpackets(hashedArea, hashed: true, ref issuerFingerprint, ref issuerKeyId);
        ReadSubpackets(unhashedArea, hashed: false, ref issuerFingerprint, ref issuerKeyId);

        // Digest = H(text || version..hashed subpackets || 0x04 0xFF || that length) (RFC 4880 §5.2.4).
        using var h = IncrementalHash.CreateHash(hash);
        h.AppendData(signedData);
        h.AppendData(p, 0, 6 + hashedLength);
        Span<byte> trailer = stackalloc byte[6];
        trailer[0] = 4;
        trailer[1] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(trailer[2..], (uint)(6 + hashedLength));
        h.AppendData(trailer);
        byte[] digest = h.GetHashAndReset();

        if (digest[0] != left0 || digest[1] != left1)
        {
            return false; // the text is not what was signed
        }

        foreach (PgpRsaKey key in trustedKeys)
        {
            if (issuerFingerprint is not null ? !key.FingerprintBytes.SequenceEqual(issuerFingerprint)
                : issuerKeyId is not null && !key.KeyId.SequenceEqual(issuerKeyId))
            {
                continue;
            }

            if (key.Verify(digest, signature, hash))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Signature subpackets (RFC 4880 §5.2.3.1). Picks out the issuer; refuses a critical
    /// subpacket it doesn't understand in the signed (hashed) area, as the RFC requires.</summary>
    private static void ReadSubpackets(ReadOnlySpan<byte> area, bool hashed, ref byte[]? issuerFingerprint, ref byte[]? issuerKeyId)
    {
        int o = 0;
        while (o < area.Length)
        {
            int first = area[o++];
            long length;
            if (first < 192)
            {
                length = first;
            }
            else if (first < 255)
            {
                if (o >= area.Length)
                {
                    throw Fail("its signature is malformed");
                }

                length = ((first - 192) << 8) + area[o++] + 192;
            }
            else
            {
                if (o + 4 > area.Length)
                {
                    throw Fail("its signature is malformed");
                }

                length = BinaryPrimitives.ReadUInt32BigEndian(area[o..]);
                o += 4;
            }

            if (length < 1 || length > area.Length - o)
            {
                throw Fail("its signature is malformed");
            }

            int type = area[o];
            ReadOnlySpan<byte> data = area.Slice(o + 1, (int)length - 1);
            o += (int)length;

            bool critical = (type & 0x80) != 0;
            switch (type & 0x7F)
            {
                case 2: // signature creation time
                    break;
                case 16: // issuer key ID
                    if (data.Length == 8)
                    {
                        issuerKeyId ??= data.ToArray();
                    }

                    break;
                case 33: // issuer fingerprint: key version, then the fingerprint
                    if (data.Length == 21 && data[0] == 4)
                    {
                        issuerFingerprint ??= data[1..].ToArray();
                    }

                    break;
                default:
                    if (critical && hashed)
                    {
                        throw Fail("its signature has a critical feature this app does not support");
                    }

                    break;
            }
        }
    }

    /// <summary>A v4 RSA public key packet → key, or null for any other kind of key.</summary>
    private static PgpRsaKey? ReadRsaKey(byte[] body)
    {
        // version (4) | creation time (4) | algorithm (1) | MPI n | MPI e
        if (body.Length < 6 || body[0] != 4 || body[5] is not (AlgoRsa or AlgoRsaSignOnly))
        {
            return null;
        }

        int o = 6;
        byte[] modulus = ReadMpi(body, ref o);
        byte[] exponent = ReadMpi(body, ref o);
        if (o != body.Length || body.Length > ushort.MaxValue)
        {
            throw Fail("the key is malformed");
        }

        // v4 fingerprint = SHA-1(0x99 || two-octet length || key packet body) (RFC 4880 §12.2).
        // SHA-1 is what the format defines; matching a fingerprint pinned in the code against it
        // would need a second preimage, which SHA-1's known weaknesses do not provide.
        byte[] prefix = [0x99, (byte)(body.Length >> 8), (byte)body.Length];
#pragma warning disable CA5350 // SHA-1 is mandated by the OpenPGP v4 fingerprint format
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350
        sha1.AppendData(prefix);
        sha1.AppendData(body);
        return new PgpRsaKey(sha1.GetHashAndReset(), modulus, exponent);
    }

    /// <summary>An OpenPGP multiprecision integer: a two-octet bit count, then the bytes.</summary>
    private static byte[] ReadMpi(ReadOnlySpan<byte> p, ref int o)
    {
        if (o + 2 > p.Length)
        {
            throw Fail("a number in it is truncated");
        }

        int bits = BinaryPrimitives.ReadUInt16BigEndian(p[o..]);
        o += 2;
        int bytes = (bits + 7) / 8;
        if (bytes == 0 || bytes > p.Length - o)
        {
            throw Fail("a number in it is truncated");
        }

        byte[] value = p.Slice(o, bytes).ToArray();
        o += bytes;
        return value;
    }

    /// <summary>Split binary OpenPGP data into (tag, body) packets. Old and new packet formats;
    /// indeterminate and partial lengths are refused (neither occurs in keys or signatures).</summary>
    private static List<(int tag, byte[] body)> ReadPackets(byte[] data)
    {
        var packets = new List<(int, byte[])>();
        int o = 0;
        while (o < data.Length)
        {
            int ctb = data[o++];
            if ((ctb & 0x80) == 0)
            {
                throw Fail("it is not OpenPGP data");
            }

            int tag;
            long length;
            if ((ctb & 0x40) == 0)
            {
                // Old format: tag in bits 5-2, length type in bits 1-0.
                tag = (ctb >> 2) & 0x0F;
                int lengthBytes = (ctb & 3) switch
                {
                    0 => 1,
                    1 => 2,
                    2 => 4,
                    _ => throw Fail("it uses an unsupported packet length"),
                };
                if (o + lengthBytes > data.Length)
                {
                    throw Fail("it is truncated");
                }

                length = lengthBytes switch
                {
                    1 => data[o],
                    2 => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o)),
                    _ => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o)),
                };
                o += lengthBytes;
            }
            else
            {
                // New format: tag in bits 5-0, then a one, two or five-octet length.
                tag = ctb & 0x3F;
                if (o >= data.Length)
                {
                    throw Fail("it is truncated");
                }

                int first = data[o++];
                if (first < 192)
                {
                    length = first;
                }
                else if (first < 224)
                {
                    if (o >= data.Length)
                    {
                        throw Fail("it is truncated");
                    }

                    length = ((first - 192) << 8) + data[o++] + 192;
                }
                else if (first == 255)
                {
                    if (o + 4 > data.Length)
                    {
                        throw Fail("it is truncated");
                    }

                    length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o));
                    o += 4;
                }
                else
                {
                    throw Fail("it uses an unsupported packet length");
                }
            }

            if (length > data.Length - o)
            {
                throw Fail("it is truncated");
            }

            packets.Add((tag, data.AsSpan(o, (int)length).ToArray()));
            o += (int)length;
        }

        return packets;
    }

    /// <summary>ASCII armor (RFC 4880 §6.2) starting at <paramref name="i"/>: BEGIN line, optional
    /// headers, base64 body, optional CRC-24 checksum (checked when present), END line.</summary>
    private static byte[] DecodeArmor(string[] lines, ref int i, string type)
    {
        Expect(lines, i++, $"-----BEGIN PGP {type}-----");

        // Armor headers ("Version: …", "Comment: …") up to a blank line. Tolerate a body that
        // starts right away: base64 never contains ": ".
        while (i < lines.Length && lines[i].Contains(": ", StringComparison.Ordinal))
        {
            i++;
        }

        var body = new StringBuilder();
        string? checksum = null;
        string end = $"-----END PGP {type}-----";
        for (; ; i++)
        {
            if (i >= lines.Length)
            {
                throw Fail("its armor is not terminated");
            }

            string line = lines[i].Trim();
            if (line == end)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (checksum is not null)
            {
                throw Fail("its armor is malformed");
            }

            if (line.StartsWith('='))
            {
                checksum = line[1..];
                continue;
            }

            body.Append(line);
        }

        i++; // past the END line

        byte[] data;
        try
        {
            data = Convert.FromBase64String(body.ToString());
        }
        catch (FormatException)
        {
            throw Fail("its armor is not valid base64");
        }

        if (checksum is not null)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromBase64String(checksum);
            }
            catch (FormatException)
            {
                throw Fail("its armor checksum is malformed");
            }

            int crc = Crc24(data);
            if (expected.Length != 3 || expected[0] != (byte)(crc >> 16) || expected[1] != (byte)(crc >> 8) || expected[2] != (byte)crc)
            {
                throw Fail("its armor checksum doesn't match (the file is damaged)");
            }
        }

        return data;
    }

    /// <summary>The armor checksum (RFC 4880 §6.1).</summary>
    internal static int Crc24(ReadOnlySpan<byte> data)
    {
        int crc = 0xB704CE;
        foreach (byte b in data)
        {
            crc ^= b << 16;
            for (int k = 0; k < 8; k++)
            {
                crc <<= 1;
                if ((crc & 0x1000000) != 0)
                {
                    crc ^= 0x1864CFB;
                }
            }
        }

        return crc & 0xFFFFFF;
    }

    private static string[] SplitLines(string s)
    {
        if (s.Length > 0 && s[0] == '﻿')
        {
            s = s[1..];
        }

        string[] lines = s.Split('\n');
        for (int k = 0; k < lines.Length; k++)
        {
            if (lines[k].EndsWith('\r'))
            {
                lines[k] = lines[k][..^1];
            }
        }

        return lines;
    }

    private static int SkipBlank(string[] lines, int i)
    {
        while (i < lines.Length && lines[i].Trim().Length == 0)
        {
            i++;
        }

        return i;
    }

    private static void Expect(string[] lines, int i, string expected)
    {
        if (i >= lines.Length || lines[i].TrimEnd() != expected)
        {
            throw Fail("it is not in the expected format");
        }
    }

    private static FormatProblem Fail(string why) => new(why);
}
