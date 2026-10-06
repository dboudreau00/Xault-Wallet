using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XaultWallet.Core.Security;

/// <summary>
/// On-disk container. Design goals:
///
///   1. Encrypted at rest with AES-256-GCM, key derived with Argon2id.
///   2. Duress / plausible deniability: the file ALWAYS contains a fixed number
///      of equal-sized slots (<see cref="SlotCount"/> = 2). One slot holds the
///      real profile; the other holds either the decoy profile OR, if the user
///      never set a duress password, uniformly random filler that is
///      indistinguishable from a real encrypted slot. An adversary who seizes
///      the file cannot prove whether a hidden second profile exists.
///   3. No slot ordering leak: which physical slot is "real" is randomised at
///      write time, so slot position reveals nothing.
///
/// Layout (all integers little-endian):
///
///   magic     "XVLT"                 4 bytes
///   version   0x02                   1 byte
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
///
/// VERSION 1 (0.1–0.3) has 4 KiB slots: room for a few wallets, not for many with an address book.
/// A version-1 file STAYS version 1 — read and written in its own layout, still openable by 0.3 —
/// until <see cref="UpgradeFormat"/> is called on purpose. Converting it silently would betray a
/// decoy: a slot can only be re-encrypted by its own password, so after one password converted the
/// file, the other slot would still be in the old layout, and whoever holds THAT password could
/// tell another password had been used since. <see cref="UpgradeFormat"/> carries each old slot,
/// unchanged, at the start of a version-2 slot (the rest random); a carried slot keeps opening with
/// its own password, which re-seals it in the version-2 layout. In a version-2 file, unlocking
/// tries BOTH layouts on EVERY slot (old slots are bound to version 1 in their associated data, new
/// ones to 2), so neither the bytes nor the timing tell a carried slot from filler.
/// </summary>
public sealed class VaultFile
{
    private static readonly byte[] Magic = "XVLT"u8.ToArray();
    private const byte Version = 2;
    private const byte LegacyVersion = 1;
    public const int SlotCount = 2;

    /// <summary>The format new vaults are written in.</summary>
    public const int CurrentFormat = Version;

    /// <summary>Fixed plaintext size per slot: room for many wallets, an address book and notes.</summary>
    public const int PaddedPlaintextBytes = 256 * 1024;

    /// <summary>The version-1 plaintext size (0.1–0.3).</summary>
    internal const int LegacyPaddedPlaintextBytes = 4096;

    private const int HeaderBytes = 4 + 1 + 1 + 2 + 4 + 4 + 4;
    private const int EncBlobBytes = VaultCrypto.NonceSizeBytes + VaultCrypto.TagSizeBytes + PaddedPlaintextBytes;
    private const int SlotBytes = VaultCrypto.SaltSizeBytes + EncBlobBytes;
    private const int LegacyEncBlobBytes = VaultCrypto.NonceSizeBytes + VaultCrypto.TagSizeBytes + LegacyPaddedPlaintextBytes;
    private const int LegacySlotBytes = VaultCrypto.SaltSizeBytes + LegacyEncBlobBytes;

    /// <summary>The largest payload a slot of a current-format file holds.</summary>
    public const int MaxPayloadBytes = PaddedPlaintextBytes - 4;

    /// <summary>The largest payload a slot of a version-1 file holds.</summary>
    public const int LegacyMaxPayloadBytes = LegacyPaddedPlaintextBytes - 4;

    /// <summary>Size of a current-format vault file.</summary>
    public const int FileBytes = HeaderBytes + (SlotCount * SlotBytes);

    public VaultCrypto.Argon2Parameters Argon { get; }

    /// <summary>2 (current), or 1: a vault from 0.1–0.3 not yet upgraded (see the class summary).</summary>
    public int FormatVersion => _version;

    /// <summary>The largest payload a slot of THIS file holds.</summary>
    public int PayloadCapacity => _version == Version ? MaxPayloadBytes : LegacyMaxPayloadBytes;

    /// <summary>Raw slots, each <see cref="SlotSize"/> long. slot[i] = salt || encBlob.</summary>
    private readonly byte[][] _slots;
    private byte _version;

    private VaultFile(VaultCrypto.Argon2Parameters argon, byte[][] slots, byte version)
    {
        Argon = argon;
        _slots = slots;
        _version = version;
    }

    private int SlotSize => _version == Version ? SlotBytes : LegacySlotBytes;

    private int PaddedSize => _version == Version ? PaddedPlaintextBytes : LegacyPaddedPlaintextBytes;

    /// <summary>Create a brand-new vault whose slots are all random filler.</summary>
    public static VaultFile CreateEmpty(VaultCrypto.Argon2Parameters argon)
    {
        var slots = new byte[SlotCount][];
        for (int i = 0; i < SlotCount; i++)
        {
            slots[i] = VaultCrypto.RandomBytes(SlotBytes);
        }

        return new VaultFile(argon, slots, Version);
    }

    /// <summary>
    /// Seal <paramref name="plaintext"/> into slot <paramref name="slotIndex"/> under a key derived from
    /// <paramref name="password"/> with a FRESH salt. The salt is stored with the slot; the associated
    /// data binds the ciphertext to the file version and slot index so slots cannot be swapped.
    /// </summary>
    public void WriteSlot(int slotIndex, SecureBuffer password, ReadOnlySpan<byte> plaintext)
    {
        CheckPayloadSize(plaintext);
        byte[] salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSizeBytes);
        using SecureBuffer key = VaultCrypto.DeriveKey(password, salt, Argon);
        _slots[slotIndex] = Seal(key, salt, slotIndex, plaintext);
    }

    /// <summary>
    /// Re-encrypt an opened slot with new contents, reusing the key derived while opening it (same
    /// salt, fresh random nonce), in this file's layout. No Argon2 work happens, so a policy re-seal
    /// performed during unlock (wipe-on-duress, removing 0.1's marker, re-sealing a carried slot)
    /// adds no measurable time to that unlock — a duress unlock must not be slower than a normal one.
    /// </summary>
    public void ResealSlot(OpenedSlot opened, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(opened);
        CheckPayloadSize(plaintext);
        _slots[opened.SlotIndex] = Seal(opened.Key, opened.Salt, opened.SlotIndex, plaintext);
    }

    /// <summary>Seal into a slot with a key and salt the caller already holds (an open session's, or
    /// one just derived for a new password).</summary>
    internal void SealSlot(int slotIndex, SecureBuffer key, byte[] salt, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(salt);
        CheckPayloadSize(plaintext);
        _slots[slotIndex] = Seal(key, salt, slotIndex, plaintext);
    }

    private void CheckPayloadSize(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > PayloadCapacity)
        {
            throw new ArgumentException("Payload too large for a slot.", nameof(plaintext));
        }
    }

    private byte[] Seal(SecureBuffer key, byte[] salt, int slotIndex, ReadOnlySpan<byte> plaintext)
    {
        // Build padded plaintext: [len][data][random pad]
        byte[] padded = VaultCrypto.RandomBytes(PaddedSize);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(padded, (uint)plaintext.Length);
            plaintext.CopyTo(padded.AsSpan(4));
            byte[] enc = VaultCrypto.Encrypt(key, padded, AssociatedData(_version, slotIndex));

            byte[] slot = new byte[SlotSize];
            salt.CopyTo(slot, 0);
            enc.CopyTo(slot, VaultCrypto.SaltSizeBytes);
            return slot;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(padded);
        }
    }

    /// <summary>Overwrite a slot with random filler (used for "wipe on duress" or to hide an unused slot).</summary>
    public void FillRandom(int slotIndex) => _slots[slotIndex] = VaultCrypto.RandomBytes(SlotSize);

    /// <summary>
    /// Convert a version-1 file to the current format. Each slot is carried unchanged at the start
    /// of a current-size slot (the rest random), so every password keeps opening its slot; the
    /// caller re-seals the slot it holds the key for. Does nothing to a current-format file.
    /// </summary>
    public void UpgradeFormat()
    {
        if (_version == Version)
        {
            return;
        }

        for (int i = 0; i < SlotCount; i++)
        {
            byte[] slot = VaultCrypto.RandomBytes(SlotBytes);
            _slots[i].CopyTo(slot, 0);
            _slots[i] = slot;
        }

        _version = Version;
    }

    /// <summary>
    /// Try every slot with <paramref name="password"/>. Returns the decrypted plaintext of the first
    /// slot whose GCM tag verifies, plus which slot matched — or null if none match (wrong password).
    /// Every slot is always tried (constant work) so timing does not reveal how many real slots exist.
    /// </summary>
    public (SecureBuffer plaintext, int slotIndex)? TryUnlock(SecureBuffer password)
    {
        using OpenedSlot? opened = TryOpen(password);
        return opened is null ? null : (new SecureBuffer(opened.Plaintext.Span), opened.SlotIndex);
    }

    /// <summary>
    /// Like <see cref="TryUnlock"/>, but keeps the matched slot's derived key so the caller can
    /// <see cref="ResealSlot"/> it without another key derivation. Dispose the result.
    /// </summary>
    public OpenedSlot? TryOpen(SecureBuffer password)
    {
        OpenedSlot? result = null;
        try
        {
            // Every slot is always tried — in a current-format file in both layouts (two full Argon2
            // derivations, four decryption attempts) — so timing can't tell which slot matched, in
            // which layout, or whether the other one holds anything. A version-1 file has one layout.
            bool currentFormat = _version == Version;
            for (int i = 0; i < SlotCount; i++)
            {
                byte[] salt = _slots[i].AsSpan(0, VaultCrypto.SaltSizeBytes).ToArray();
                SecureBuffer key = VaultCrypto.DeriveKey(password, salt, Argon);
                SecureBuffer? current = null;
                SecureBuffer? legacy = null;
                bool kept = false;
                try
                {
                    if (currentFormat)
                    {
                        current = VaultCrypto.TryDecrypt(key, _slots[i].AsSpan(VaultCrypto.SaltSizeBytes, EncBlobBytes), AssociatedData(Version, i));
                    }

                    legacy = VaultCrypto.TryDecrypt(key, _slots[i].AsSpan(VaultCrypto.SaltSizeBytes, LegacyEncBlobBytes), AssociatedData(LegacyVersion, i));

                    // A version-1 slot inside a current-format file is a carried one (re-sealed when
                    // its password opens it); in a version-1 file it is simply the file's layout.
                    (SecureBuffer? dec, bool isCarried) = current is not null ? (current, false) : (legacy, currentFormat);
                    if (dec is not null && result is null)
                    {
                        // Unpad: first 4 bytes are the real length.
                        uint len = BinaryPrimitives.ReadUInt32LittleEndian(dec.Span);
                        if (len <= dec.Length - 4)
                        {
                            result = new OpenedSlot(i, new SecureBuffer(dec.Span.Slice(4, (int)len)), key, salt, isCarried);
                            kept = true;
                        }
                    }
                }
                finally
                {
                    current?.Dispose();
                    legacy?.Dispose();
                    if (!kept)
                    {
                        key.Dispose();
                    }
                }
            }
        }
        catch
        {
            // A failure on a LATER slot (e.g. Argon2 running out of memory) must not strand an
            // already-decrypted payload and its key outside any using block.
            result?.Dispose();
            throw;
        }

        return result;
    }

    private static byte[] AssociatedData(byte version, int slotIndex) => [(byte)'X', (byte)'V', version, (byte)slotIndex];

    /// <summary>A copy of one slot's raw bytes (to carry it into another copy of the file).</summary>
    internal byte[] GetSlotBytes(int slotIndex) => (byte[])_slots[slotIndex].Clone();

    /// <summary>Replace one slot's raw bytes with bytes taken from another copy of this vault.</summary>
    internal void SetSlotBytes(int slotIndex, byte[] slot)
    {
        if (slot.Length != SlotSize)
        {
            throw new ArgumentException("Not a slot of this vault format.", nameof(slot));
        }

        _slots[slotIndex] = (byte[])slot.Clone();
    }

    /// <summary>The salt a slot's key is derived with (its first bytes; the same in both layouts).</summary>
    internal ReadOnlySpan<byte> SlotSalt(int slotIndex) => _slots[slotIndex].AsSpan(0, VaultCrypto.SaltSizeBytes);

    public byte[] Serialize()
    {
        byte[] buf = new byte[HeaderBytes + (SlotCount * SlotSize)];
        int o = 0;
        Magic.CopyTo(buf, o); o += 4;
        buf[o++] = _version;
        buf[o++] = SlotCount;
        buf[o++] = 0; buf[o++] = 0; // reserved
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), (uint)Argon.MemoryKib); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), (uint)Argon.Iterations); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), (uint)Argon.Parallelism); o += 4;
        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i].CopyTo(buf, o);
            o += SlotSize;
        }

        return buf;
    }

    /// <summary>Read a vault file of the current version, or of version 1 (0.1–0.3), which keeps
    /// its format (see the class summary).</summary>
    public static VaultFile Deserialize(byte[] buf)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (buf.Length < HeaderBytes || !buf.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not an XaultWallet file (bad magic).");
        }

        byte version = buf[4];
        int slotBytesInFile = version switch
        {
            Version => SlotBytes,
            LegacyVersion => LegacySlotBytes,
            _ => throw new InvalidDataException(version > Version
                ? "This vault was created by a newer version of XaultWallet. Update the app to open it."
                : $"Unsupported vault version {version}."),
        };

        if (buf.Length != HeaderBytes + (SlotCount * slotBytesInFile))
        {
            throw new InvalidDataException("Vault file has unexpected size.");
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
            slots[i] = buf.AsSpan(o, slotBytesInFile).ToArray();
            o += slotBytesInFile;
        }

        return new VaultFile(argon, slots, version);
    }
}

/// <summary>
/// A slot that a password successfully opened: its decrypted payload plus the key derived for it,
/// kept only so the slot can be re-sealed without repeating the key derivation. Dispose promptly —
/// disposal zeroes both the payload and the key.
/// </summary>
public sealed class OpenedSlot : IDisposable
{
    internal OpenedSlot(int slotIndex, SecureBuffer plaintext, SecureBuffer key, byte[] salt, bool isLegacyLayout = false)
    {
        SlotIndex = slotIndex;
        Plaintext = plaintext;
        Key = key;
        Salt = salt;
        IsLegacyLayout = isLegacyLayout;
    }

    /// <summary>Physical slot position (0 or 1). Position carries no meaning: it is randomised at creation.</summary>
    public int SlotIndex { get; }

    /// <summary>The unpadded slot payload.</summary>
    public SecureBuffer Plaintext { get; }

    /// <summary>A version-1 slot carried inside a current-format file (see <see cref="VaultFile.UpgradeFormat"/>):
    /// it should be re-sealed in the current layout now that its password opened it.</summary>
    public bool IsLegacyLayout { get; }

    internal SecureBuffer Key { get; }

    internal byte[] Salt { get; }

    public void Dispose()
    {
        Plaintext.Dispose();
        Key.Dispose();
    }
}
