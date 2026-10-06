using System.Security.Cryptography;
using XaultWallet.Core.Models;

namespace XaultWallet.Core.Security;

/// <summary>Outcome of a successful unlock. Deliberately carries no "was this the decoy?" flag:
/// the vault itself cannot tell — that is the point of the design.</summary>
/// <param name="Profile">Everything the password opens.</param>
/// <param name="UpgradedFromLegacyFormat">The opened slot was in the 0.1 (v1) payload format and has
/// just been re-sealed in the current one. Set for EITHER slot alike, so it says nothing about which
/// one opened — only that the vault predates 0.2, and that any other slot is still in the old format
/// until its own password is used.</param>
public sealed record UnlockResult(WalletProfile Profile, bool UpgradedFromLegacyFormat = false)
{
    /// <summary>The wallet that opens first (the last one used).</summary>
    public WalletSecrets Secrets => Profile.ActiveWallet!;
}

/// <summary>
/// Coordinates the vault file, its two slots and the duress policy. This class never compares
/// passwords in plaintext: it asks <see cref="VaultFile"/> to try decrypting each slot.
///
/// Every operation is SYMMETRIC: unlock and change password behave identically for whichever slot a
/// password opens, and the slot payloads carry no real/decoy marker. Nothing the app does — and
/// nothing an examiner can decrypt with the duress password — distinguishes the decoy from the only
/// profile of a single-profile vault. (Narrow, documented exception: a decoy whose wipe-on-duress
/// flag has not fired yet. See <see cref="WalletProfile.WipeOtherSlotOnUnlock"/>.)
/// </summary>
public sealed class VaultManager
{
    private readonly string _path;
    private VaultFile _file;
    private readonly object _gate = new();

    private VaultManager(string path, VaultFile file)
    {
        _path = path;
        _file = file;
    }

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>Create a vault holding one wallet, plus optionally a one-wallet decoy profile.</summary>
    public static VaultManager Create(
        string path,
        SecureBuffer mainPassword,
        WalletSecrets mainSecrets,
        SecureBuffer? duressPassword = null,
        WalletSecrets? duressSecrets = null,
        VaultCrypto.Argon2Parameters? argon = null)
    {
        ArgumentNullException.ThrowIfNull(mainSecrets);
        if (mainSecrets.WipeOtherSlotOnUnlock)
        {
            throw new ArgumentException("Only the decoy wallet can carry wipe-on-duress.", nameof(mainSecrets));
        }

        return Create(path, mainPassword, WalletProfile.OfOne(mainSecrets), duressPassword,
            duressSecrets is null ? null : WalletProfile.OfOne(duressSecrets), argon);
    }

    /// <summary>
    /// Create a new vault. If <paramref name="duressPassword"/>/<paramref name="duressProfile"/> are
    /// supplied, a decoy profile is stored in the second slot; otherwise the second slot is random
    /// filler that cannot be distinguished from an encrypted slot.
    /// </summary>
    public static VaultManager Create(
        string path,
        SecureBuffer mainPassword,
        WalletProfile mainProfile,
        SecureBuffer? duressPassword,
        WalletProfile? duressProfile,
        VaultCrypto.Argon2Parameters? argon = null)
    {
        ArgumentNullException.ThrowIfNull(mainPassword);
        ArgumentNullException.ThrowIfNull(mainProfile);

        if (Exists(path))
        {
            throw new IOException($"A vault already exists at {path}.");
        }

        if (mainPassword.Length == 0)
        {
            throw new ArgumentException("The main password must not be empty.", nameof(mainPassword));
        }

        if (mainProfile.Wallets.Count == 0)
        {
            throw new ArgumentException("A vault needs at least one wallet.", nameof(mainProfile));
        }

        // A main profile that wipes the other slot would destroy the decoy on every normal unlock.
        if (mainProfile.WipeOtherSlotOnUnlock)
        {
            throw new ArgumentException("Only the decoy wallet can carry wipe-on-duress.", nameof(mainProfile));
        }

        // A half-specified duress profile is a caller bug that would silently create a vault
        // with NO decoy while the user believes one exists. Fail loudly instead.
        if (duressPassword is null != duressProfile is null)
        {
            throw new ArgumentException(
                "Supply both a duress password and duress secrets, or neither.", nameof(duressProfile));
        }

        if (duressPassword is not null && duressProfile is not null)
        {
            if (duressProfile.Wallets.Count == 0)
            {
                throw new ArgumentException("The decoy needs at least one wallet.", nameof(duressProfile));
            }

            // Identical passwords make every unlock a coin flip between the two slots — and with
            // wipe-on-duress enabled, a "normal" unlock could permanently destroy the real wallet.
            if (PasswordsEqual(mainPassword, duressPassword))
            {
                throw new ArgumentException(
                    "The duress password must be different from the main password.", nameof(duressPassword));
            }

            // The same seed in both profiles means the "decoy" opens the real funds, silently
            // defeating the entire duress feature.
            if (mainProfile.Wallets.Any(m => duressProfile.Wallets.Any(d => SameWallet(m, d))))
            {
                throw new ArgumentException(
                    "The decoy seed must be different from the real wallet's seed.", nameof(duressProfile));
            }
        }

        var file = VaultFile.CreateEmpty(argon ?? VaultCrypto.Argon2Parameters.Default);

        // Randomise which physical slot holds the real profile so position leaks nothing.
        int realSlot = RandomNumberGenerator.GetInt32(VaultFile.SlotCount);

        WriteSlotZeroing(file, realSlot, mainPassword, mainProfile);

        if (duressPassword is not null && duressProfile is not null)
        {
            WriteSlotZeroing(file, OtherSlot(realSlot), duressPassword, duressProfile);
        }
        // else: the other slot keeps its random filler from CreateEmpty.

        var mgr = new VaultManager(path, file);
        mgr.Persist(file);
        return mgr;
    }

    public static VaultManager Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Vault path is empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Vault file not found.", path);
        }

        return new VaultManager(path, ReadFile(path));
    }

    private static VaultFile ReadFile(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new IOException("Could not read the vault file. It may be open in another program.", ex);
        }

        return VaultFile.Deserialize(bytes);
    }

    /// <summary>
    /// Try to unlock with the given password. Returns null on a wrong password. Applies the opened
    /// slot's on-open policy (wipe-on-duress, format migration) before returning. For a profile the
    /// app keeps open and changes, use <see cref="OpenSession"/>.
    /// </summary>
    public UnlockResult? Unlock(SecureBuffer password)
    {
        Opened? hit = OpenAndApplyPolicy(password);
        if (hit is null)
        {
            return null;
        }

        using (hit.Slot)
        {
            return new UnlockResult(hit.Profile, hit.From01);
        }
    }

    /// <summary>
    /// Unlock and keep the profile open: a <see cref="VaultSession"/> can save changes to it (wallets
    /// added, renamed, removed; contacts; labels and notes) without asking for the password again.
    /// Returns null on a wrong password. Dispose the session on lock: that zeroes its key.
    /// </summary>
    public VaultSession? OpenSession(SecureBuffer password)
    {
        Opened? hit = OpenAndApplyPolicy(password);
        if (hit is null)
        {
            return null;
        }

        try
        {
            return new VaultSession(this, hit.Slot, hit.Profile, hit.From01);
        }
        catch
        {
            hit.Slot.Dispose();
            throw;
        }
    }

    /// <summary>Open whichever slot the password decrypts, apply its policy and persist the result
    /// (quietly: see below).</summary>
    private Opened? OpenAndApplyPolicy(SecureBuffer password)
    {
        lock (_gate)
        {
            Opened? hit = OpenWithPolicy(password);
            if (hit is null)
            {
                return null;
            }

            if (hit.Wiped || hit.Migrated)
            {
                // Best-effort and SILENT. If the disk write fails (file locked by AV/backup software,
                // read-only media, disk full) the wallet must STILL open normally: an error here would
                // make a duress unlock visibly different from a normal one at exactly the moment
                // indistinguishability matters most. Deliberately not logged either — a log line
                // timestamped at the duress unlock would tell the same story to anyone reading it.
                PersistQuietly();
            }

            if (hit.Wiped)
            {
                ShredSiblingCopies();
            }

            return hit;
        }
    }

    /// <summary>
    /// Re-encrypt the profile that <paramref name="currentPassword"/> opens under
    /// <paramref name="newPassword"/>. Works identically for EITHER slot, so changing a password
    /// under coercion reveals nothing about which profile it belongs to (an asymmetric rule —
    /// "only the real wallet's password works here" — would announce that a real wallet exists).
    /// Returns false on a wrong current password.
    /// </summary>
    /// <exception cref="ArgumentException">The new password is empty, or would also open the
    /// other slot (the message is deliberately neutral).</exception>
    public bool ChangePassword(SecureBuffer currentPassword, SecureBuffer newPassword)
    {
        ArgumentNullException.ThrowIfNull(newPassword);
        if (newPassword.Length == 0)
        {
            throw new ArgumentException("The new password must not be empty.", nameof(newPassword));
        }

        lock (_gate)
        {
            Opened? hit = OpenWithPolicy(currentPassword);
            if (hit is null)
            {
                return false;
            }

            using (hit.Slot)
            {
                RefuseCollision(newPassword, hit.Slot.SlotIndex);
                WriteSlotZeroing(_file, hit.Slot.SlotIndex, newPassword, hit.Profile);
                Persist(_file);
                if (hit.Wiped)
                {
                    ShredSiblingCopies();
                }

                return true;
            }
        }
    }

    /// <summary>Refuse a new password that also opens the OTHER slot: one password matching both
    /// slots makes unlock ambiguous (and could fire wipe-on-duress on a "normal" unlock).</summary>
    private void RefuseCollision(SecureBuffer newPassword, int ownSlot)
    {
        var collision = _file.TryUnlock(newPassword);
        if (collision is { } c)
        {
            c.plaintext.Dispose();
            if (c.slotIndex != ownSlot)
            {
                throw new ArgumentException(
                    "That new password can't be used for this vault. Choose a different one.", nameof(newPassword));
            }
        }
    }

    // ------------------------------------------------------------------ for VaultSession

    /// <summary>Seal <paramref name="profile"/> into an open slot with the session's key and write it.
    /// Starts from the file as it is on disk NOW, replacing only this slot, so whatever the other
    /// slot holds on disk is kept byte for byte.</summary>
    internal void SaveSlot(int slotIndex, SecureBuffer key, byte[] salt, WalletProfile profile)
    {
        byte[] payload = SlotPayload.Serialize(profile);
        try
        {
            SaveSlotPayload(slotIndex, key, salt, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Seal an already serialized profile into its slot (the caller zeroes the payload).</summary>
    internal void SaveSlotPayload(int slotIndex, SecureBuffer key, byte[] salt, byte[] payload)
    {
        if (payload.Length > VaultFile.MaxPayloadBytes)
        {
            throw new VaultFullException();
        }

        lock (_gate)
        {
            VaultFile current = CurrentFileForWrite();
            current.SealSlot(slotIndex, key, salt, payload);
            Persist(current);
            _file = current;
        }
    }

    /// <summary>Re-encrypt an open slot under a new password (fresh salt). Returns the new key and salt
    /// for the session to keep. False when <paramref name="currentPassword"/> doesn't open THIS slot.</summary>
    internal (SecureBuffer key, byte[] salt)? ChangeSlotPassword(int slotIndex, SecureBuffer currentPassword, SecureBuffer newPassword, WalletProfile profile)
    {
        ArgumentNullException.ThrowIfNull(newPassword);
        if (newPassword.Length == 0)
        {
            throw new ArgumentException("The new password must not be empty.", nameof(newPassword));
        }

        lock (_gate)
        {
            VaultFile current = CurrentFileForWrite();
            _file = current;
            if (!OpensSlot(currentPassword, slotIndex))
            {
                return null;
            }

            RefuseCollision(newPassword, slotIndex);

            byte[] payload = SlotPayload.Serialize(profile);
            byte[] salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSizeBytes);
            SecureBuffer key = VaultCrypto.DeriveKey(newPassword, salt, current.Argon);
            try
            {
                current.SealSlot(slotIndex, key, salt, payload);
                Persist(current);
                return (key, salt);
            }
            catch
            {
                key.Dispose();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    /// <summary>True when <paramref name="password"/> opens exactly slot <paramref name="slotIndex"/>
    /// (both slots are always tried, as for an unlock). No policy runs: this only checks.</summary>
    internal bool OpensSlot(SecureBuffer password, int slotIndex)
    {
        using OpenedSlot? opened = _file.TryOpen(password);
        return opened is not null && opened.SlotIndex == slotIndex;
    }

    private VaultFile CurrentFileForWrite()
    {
        if (!File.Exists(_path))
        {
            return _file; // deleted underneath an open session: write back what we hold
        }

        VaultFile onDisk = ReadFile(_path);
        if (onDisk.Argon != _file.Argon)
        {
            throw new IOException("The vault file was replaced while it was open. Lock and unlock again.");
        }

        return onDisk;
    }

    // ------------------------------------------------------------------ policy

    /// <summary>A slot opened by a password, after its on-open policy ran (in memory only).</summary>
    private sealed record Opened(OpenedSlot Slot, WalletProfile Profile, bool Wiped, bool Migrated, bool From01);

    /// <summary>
    /// Open whichever slot <paramref name="password"/> decrypts and apply its on-open policy to the
    /// in-memory file: wipe-on-duress (randomise the other slot and clear the flag, so the vault now
    /// looks like a single-profile vault) and format migration (an old payload, or a slot still in the
    /// 0.1–0.3 file layout, is re-sealed in the current ones). Both re-seal the opened slot with the
    /// key already derived, so no extra Argon2 work happens. The caller persists.
    /// Every operation that proves possession of a slot's password goes through here, so
    /// wipe-on-duress fires on ANY use of the duress password, not only on unlock.
    /// </summary>
    private Opened? OpenWithPolicy(SecureBuffer password)
    {
        ArgumentNullException.ThrowIfNull(password);
        OpenedSlot? slot = _file.TryOpen(password);
        if (slot is null)
        {
            return null;
        }

        try
        {
            WalletProfile profile = SlotPayload.Deserialize(slot.Plaintext.Span, out int version);

            bool wiped = false;
            if (profile.WipeOtherSlotOnUnlock)
            {
                _file.FillRandom(OtherSlot(slot.SlotIndex));
                profile.WipeOtherSlotOnUnlock = false; // consumed
                wiped = true;
            }

            bool migrated = version < SlotPayload.CurrentVersion || slot.IsLegacyLayout;
            if (wiped || migrated)
            {
                byte[] payload = SlotPayload.Serialize(profile);
                try
                {
                    _file.ResealSlot(slot, payload);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }

            return new Opened(slot, profile, wiped, migrated, version == SlotPayload.Version01);
        }
        catch
        {
            slot.Dispose();
            throw;
        }
    }

    private static int OtherSlot(int slotIndex) => slotIndex == 0 ? 1 : 0;

    private void PersistQuietly()
    {
        try
        {
            Persist(_file);
        }
        catch
        {
            try { Persist(_file); } catch { /* second attempt; then give up silently */ }
        }
    }

    /// <summary>
    /// After a duress wipe, also destroy earlier copies of this vault that the app itself left
    /// beside it (Settings → Restore keeps the replaced vault as "vault.xv.replaced-*"). Those
    /// copies may still hold the profile the wipe just destroyed — "wipe the real wallet on this
    /// device" would be a broken promise without this. Exports saved elsewhere are out of reach.
    /// </summary>
    private void ShredSiblingCopies()
    {
        try
        {
            string full = Path.GetFullPath(_path);
            string? dir = Path.GetDirectoryName(full);
            if (dir is null)
            {
                return;
            }

            foreach (string copy in Directory.EnumerateFiles(dir, Path.GetFileName(full) + ".replaced-*"))
            {
                SecureDelete.File(copy);
            }
        }
        catch
        {
            // best effort and silent, like the wipe itself
        }
    }

    private void Persist(VaultFile file)
    {
        byte[] data = file.Serialize();
        string tmp = _path + ".tmp";

        try
        {
            // Write + flush to disk so the bytes are durable before we swap the file in.
            using (FileStream fs = PrivateFiles.OpenWrite(tmp)) // 0600: the vault is offline-attackable
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(flushToDisk: true);
            }

            // Atomic replace so a crash mid-write cannot corrupt the live vault.
            if (File.Exists(_path))
            {
                File.Replace(tmp, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, _path);
            }
        }
        catch
        {
            TryDelete(tmp); // never leave a half-written temp file behind
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Serialize the profile, hand it to the slot, then ZERO the serialized JSON — it holds the
    /// mnemonics in cleartext, and leaving it for the GC would undercut the SecureBuffer discipline
    /// used everywhere else. (The slot sealing zeroes its own padded copy.)
    /// </summary>
    private static void WriteSlotZeroing(VaultFile file, int slotIndex, SecureBuffer password, WalletProfile profile)
    {
        byte[] payload = SlotPayload.Serialize(profile);
        try
        {
            if (payload.Length > VaultFile.MaxPayloadBytes)
            {
                throw new VaultFullException();
            }

            file.WriteSlot(slotIndex, password, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Constant-time password comparison (false for different lengths).</summary>
    private static bool PasswordsEqual(SecureBuffer a, SecureBuffer b) =>
        CryptographicOperations.FixedTimeEquals(a.Span, b.Span);

    /// <summary>The same wallet restored two ways counts too: equal seeds, or equal addresses.</summary>
    private static bool SameWallet(WalletSecrets a, WalletSecrets b) =>
        (a.Mnemonic.Length > 0 && MnemonicsEqual(a.Mnemonic, b.Mnemonic))
        || (a.Address.Length > 0 && string.Equals(a.Address.Trim(), b.Address.Trim(), StringComparison.Ordinal));

    /// <summary>Whitespace- and case-insensitive mnemonic comparison.</summary>
    private static bool MnemonicsEqual(string? a, string? b)
    {
        static string Norm(string? m) => string.Join(' ',
            (m ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return Norm(a) == Norm(b);
    }
}

/// <summary>The profile no longer fits in its vault slot.</summary>
public sealed class VaultFullException() : IOException(
    "The vault is full: it can't hold more wallets, contacts or notes. Remove some, then try again.");
