using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XaultWallet.Core.Security;

/// <summary>
/// On-disk container. Design goals:
///
///   1. Encrypted at rest with AES-256-GCM, key derived with Argon2id.
///   2. Duress / plausible deniability: the file ALWAYS contains a fixed number
///      of equal-sized slots (<see cref="SlotCount"/> = 2). One slot holds the
///      real wallet; the other holds either the decoy wallet OR, if the user
///      never set a duress password, uniformly random filler that is
///      indistinguishable from a real encrypted slot. An adversary who seizes
///      the file cannot prove whether a hidden second wallet exists.
///   3. No slot ordering leak: which physical slot is "real" is randomised at
///      write time, so slot position reveals nothing.
///
/// Layout (all integers little-endian):
///
///   magic     "XVLT"                 4 bytes
///   version   0x01                   1 byte
///   slotCount 0x02                   1 byte
///   reserved  0x0000                 2 bytes
///   argonMem  uint32 (KiB)           4 bytes   } KDF parameters are public;
///   argonIt   uint32                 4 bytes   } they are not secret and are
///   argonPar  uint32                 4 bytes   } needed to re-derive the key.
///   ── then SlotCount slots, each exactly SlotBytes long ──
///     salt      16 bytes
///     encBlob   (nonce 12 | tag 16 | ciphertext PaddedPlaintextBytes)
///
/// The plaintext inside each slot is: [uint32 realLength][JSON bytes][random padding]
/// padded up to <see cref="PaddedPlaintextBytes"/> so every slot is identical size.
/// </summary>
public sealed class VaultFile
{
    private static readonly byte[] Magic = "XVLT"u8.ToArray();
    private const byte Version = 1;
    public const int SlotCount = 2;

    /// <summary>Fixed plaintext size per slot (4 KiB is ample for a seed + metadata).</summary>
    public const int PaddedPlaintextBytes = 4096;

    private const int HeaderBytes = 4 + 1 + 1 + 2 + 4 + 4 + 4;
    private const int EncBlobBytes = VaultCrypto.NonceSizeBytes + VaultCrypto.TagSizeBytes + PaddedPlaintextBytes;
    private const int SlotBytes = VaultCrypto.SaltSizeBytes + EncBlobBytes;

    public VaultCrypto.Argon2Parameters Argon { get; }

    /// <summary>Raw slots. slot[i] = salt || encBlob.</summary>
    private readonly byte[][] _slots;

    private VaultFile(VaultCrypto.Argon2Parameters argon, byte[][] slots)
    {
        Argon = argon;
        _slots = slots;
    }

    /// <summary>Create a brand-new vault whose slots are all random filler.</summary>
    public static VaultFile CreateEmpty(VaultCrypto.Argon2Parameters argon)
    {
        var slots = new byte[SlotCount][];
        for (int i = 0; i < SlotCount; i++)
        {
            slots[i] = VaultCrypto.RandomBytes(SlotBytes);
        }

        return new VaultFile(argon, slots);
    }

    /// <summary>
    /// Seal <paramref name="plaintext"/> into slot <paramref name="slotIndex"/> under a key derived from
    /// <paramref name="password"/>. The salt is stored with the slot; the associated data binds the
    /// ciphertext to the file version and slot index so slots cannot be swapped.
    /// </summary>
    public void WriteSlot(int slotIndex, SecureBuffer password, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > PaddedPlaintextBytes - 4)
        {
            throw new ArgumentException("Payload too large for a slot.", nameof(plaintext));
        }

        // Build padded plaintext: [len][data][random pad]
        byte[] padded = VaultCrypto.RandomBytes(PaddedPlaintextBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(padded, (uint)plaintext.Length);
        plaintext.CopyTo(padded.AsSpan(4));

        byte[] salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSizeBytes);
        using SecureBuffer key = VaultCrypto.DeriveKey(password, salt, Argon);
        byte[] enc = VaultCrypto.Encrypt(key, padded, AssociatedData(slotIndex));
        CryptographicOperations.ZeroMemory(padded);

        byte[] slot = new byte[SlotBytes];
        salt.CopyTo(slot, 0);
        enc.CopyTo(slot, VaultCrypto.SaltSizeBytes);
        _slots[slotIndex] = slot;
    }

    /// <summary>Overwrite a slot with random filler (used for "wipe on duress" or to hide an unused slot).</summary>
    public void FillRandom(int slotIndex) => _slots[slotIndex] = VaultCrypto.RandomBytes(SlotBytes);

    /// <summary>
    /// Try every slot with <paramref name="password"/>. Returns the decrypted plaintext of the first
    /// slot whose GCM tag verifies, plus which slot matched — or null if none match (wrong password).
    /// Every slot is always tried (constant work) so timing does not reveal how many real slots exist.
    /// </summary>
    public (SecureBuffer plaintext, int slotIndex)? TryUnlock(SecureBuffer password)
    {
        (SecureBuffer, int)? result = null;

        for (int i = 0; i < SlotCount; i++)
        {
            byte[] salt = _slots[i].AsSpan(0, VaultCrypto.SaltSizeBytes).ToArray();
            ReadOnlySpan<byte> enc = _slots[i].AsSpan(VaultCrypto.SaltSizeBytes);

            using SecureBuffer key = VaultCrypto.DeriveKey(password, salt, Argon);
            SecureBuffer? dec = VaultCrypto.TryDecrypt(key, enc, AssociatedData(i));

            if (dec is not null && result is null)
            {
                // Unpad: first 4 bytes are the real length.
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(dec.Span);
                if (len <= PaddedPlaintextBytes - 4)
                {
                    var payload = new SecureBuffer(dec.Span.Slice(4, (int)len));
                    result = (payload, i);
                }

                dec.Dispose();
            }
            else
            {
                dec?.Dispose();
            }
        }

        return result;
    }

    private static byte[] AssociatedData(int slotIndex) => new[] { (byte)'X', (byte)'V', Version, (byte)slotIndex };

    public byte[] Serialize()
    {
        byte[] buf = new byte[HeaderBytes + SlotCount * SlotBytes];
        int o = 0;
        Magic.CopyTo(buf, o); o += 4;
        buf[o++] = Version;
        buf[o++] = SlotCount;
        buf[o++] = 0; buf[o++] = 0; // reserved
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), (uint)Argon.MemoryKib); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), (uint)Argon.Iterations); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), (uint)Argon.Parallelism); o += 4;
        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i].CopyTo(buf, o);
            o += SlotBytes;
        }

        return buf;
    }

    public static VaultFile Deserialize(byte[] buf)
    {
        if (buf.Length != HeaderBytes + SlotCount * SlotBytes)
        {
            throw new InvalidDataException("Vault file has unexpected size.");
        }

        if (!buf.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not an XaultWallet file (bad magic).");
        }

        if (buf[4] != Version)
        {
            throw new InvalidDataException($"Unsupported vault version {buf[4]}.");
        }

        if (buf[5] != SlotCount)
        {
            throw new InvalidDataException("Unexpected slot count.");
        }

        int o = 8;
        int mem = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(o)); o += 4;
        int it = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(o)); o += 4;
        int par = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(o)); o += 4;

        var argon = new VaultCrypto.Argon2Parameters(mem, it, par);
        try
        {
            // Reject absurd KDF parameters from a corrupted/malicious file before they can be
            // used to force a huge allocation on unlock.
            argon.Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("Vault header has invalid KDF parameters.", ex);
        }

        var slots = new byte[SlotCount][];
        for (int i = 0; i < SlotCount; i++)
        {
            slots[i] = buf.AsSpan(o, SlotBytes).ToArray();
            o += SlotBytes;
        }

        return new VaultFile(argon, slots);
    }
}
