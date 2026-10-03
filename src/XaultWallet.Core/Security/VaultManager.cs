using System.Security.Cryptography;
using XaultWallet.Core.Models;

namespace XaultWallet.Core.Security;

/// <summary>Outcome of a successful unlock. Deliberately carries no "was this the decoy?" flag:
/// the vault itself cannot tell — that is the point of the design.</summary>
public sealed record UnlockResult(WalletSecrets Secrets);

/// <summary>
/// Coordinates the vault file, its two slots and the duress policy. This class never compares
/// passwords in plaintext: it asks <see cref="VaultFile"/> to try decrypting each slot.
///
/// Every operation is SYMMETRIC: unlock, change password and change node behave identically for
/// whichever slot a password opens, and the slot payloads carry no real/decoy marker. Nothing the
/// app does — and nothing an examiner can decrypt with the duress password — distinguishes the
/// decoy from the only wallet of a single-wallet vault. (Narrow, documented exception: a decoy whose
/// wipe-on-duress flag has not fired yet. See <see cref="WalletSecrets.WipeOtherSlotOnUnlock"/>.)
/// </summary>
public sealed class VaultManager
{
    private readonly string _path;
    private readonly VaultFile _file;

    private VaultManager(string path, VaultFile file)
    {
        _path = path;
        _file = file;
    }

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>
    /// Create a new vault. If <paramref name="duressPassword"/>/<paramref name="duressSecrets"/> are
    /// supplied, a decoy wallet is stored in the second slot; otherwise the second slot is random
    /// filler that cannot be distinguished from an encrypted slot.
    /// </summary>
    public static VaultManager Create(
        string path,
        SecureBuffer mainPassword,
        WalletSecrets mainSecrets,
        SecureBuffer? duressPassword = null,
        WalletSecrets? duressSecrets = null,
        VaultCrypto.Argon2Parameters? argon = null)
    {
        ArgumentNullException.ThrowIfNull(mainPassword);
        ArgumentNullException.ThrowIfNull(mainSecrets);

        if (Exists(path))
        {
            throw new IOException($"A vault already exists at {path}.");
        }

        if (mainPassword.Length == 0)
        {
            throw new ArgumentException("The main password must not be empty.", nameof(mainPassword));
        }

        // A main wallet that wipes the other slot would destroy the decoy on every normal unlock.
        if (mainSecrets.WipeOtherSlotOnUnlock)
        {
            throw new ArgumentException("Only the decoy wallet can carry wipe-on-duress.", nameof(mainSecrets));
        }

        // A half-specified duress profile is a caller bug that would silently create a vault
        // with NO decoy while the user believes one exists. Fail loudly instead.
        if (duressPassword is null != duressSecrets is null)
        {
            throw new ArgumentException(
                "Supply both a duress password and duress secrets, or neither.", nameof(duressSecrets));
        }

        if (duressPassword is not null && duressSecrets is not null)
        {
            // Identical passwords make every unlock a coin flip between the two slots — and with
            // wipe-on-duress enabled, a "normal" unlock could permanently destroy the real wallet.
            if (PasswordsEqual(mainPassword, duressPassword))
            {
                throw new ArgumentException(
                    "The duress password must be different from the main password.", nameof(duressPassword));
            }

            // The same seed in both slots means the "decoy" opens the real funds, silently
            // defeating the entire duress feature.
            if (MnemonicsEqual(mainSecrets.Mnemonic, duressSecrets.Mnemonic))
            {
                throw new ArgumentException(
                    "The decoy seed must be different from the real wallet's seed.", nameof(duressSecrets));
            }
        }

        var file = VaultFile.CreateEmpty(argon ?? VaultCrypto.Argon2Parameters.Default);

        // Randomise which physical slot holds the real wallet so position leaks nothing.
        int realSlot = RandomNumberGenerator.GetInt32(VaultFile.SlotCount);

        WriteSlotZeroing(file, realSlot, mainPassword, mainSecrets);

        if (duressPassword is not null && duressSecrets is not null)
        {
            WriteSlotZeroing(file, OtherSlot(realSlot), duressPassword, duressSecrets);
        }
        // else: the other slot keeps its random filler from CreateEmpty.

        var mgr = new VaultManager(path, file);
        mgr.Persist();
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

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new IOException("Could not read the vault file. It may be open in another program.", ex);
        }

        return new VaultManager(path, VaultFile.Deserialize(bytes));
    }

    /// <summary>
    /// Try to unlock with the given password. Returns null on a wrong password. Applies the opened
    /// slot's on-open policy (wipe-on-duress, legacy-format migration) before returning.
    /// </summary>
    public UnlockResult? Unlock(SecureBuffer password)
    {
        Opened? hit = OpenWithPolicy(password);
        if (hit is null)
        {
            return null;
        }

        using (hit.Slot)
        {
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

            return new UnlockResult(hit.Secrets);
        }
    }

    /// <summary>
    /// Re-encrypt the wallet that <paramref name="currentPassword"/> opens under
    /// <paramref name="newPassword"/>. Works identically for EITHER slot, so changing a password
    /// under coercion reveals nothing about which wallet it belongs to (an asymmetric rule —
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

        Opened? hit = OpenWithPolicy(currentPassword);
        if (hit is null)
        {
            return false;
        }

        using (hit.Slot)
        {
            // Refuse a new password that also opens the OTHER slot: one password matching both
            // slots makes unlock ambiguous (and could fire wipe-on-duress on a "normal" unlock).
            var collision = _file.TryUnlock(newPassword);
            if (collision is { } c)
            {
                c.plaintext.Dispose();
                if (c.slotIndex != hit.Slot.SlotIndex)
                {
                    throw new ArgumentException(
                        "That new password can't be used for this vault. Choose a different one.",
                        nameof(newPassword));
                }
            }

            WriteSlotZeroing(_file, hit.Slot.SlotIndex, newPassword, hit.Secrets);
            Persist();
            if (hit.Wiped)
            {
                ShredSiblingCopies();
            }

            return true;
        }
    }

    /// <summary>
    /// Re-point the wallet that <paramref name="password"/> opens at a new daemon (node) address,
    /// then re-seal that same slot. Works identically for either slot, so the operation reveals
    /// nothing about which profile is which. The other slot's bytes are left untouched.
    /// Returns false on a wrong password.
    /// </summary>
    /// <exception cref="ArgumentException">The address is not a valid http(s) URL.</exception>
    public bool ChangeDaemonAddress(SecureBuffer password, string newDaemonAddress)
    {
        // Validate before doing any KDF work. Address validity is independent of the password, so
        // rejecting a bad URL up front leaks nothing and avoids a needless Argon2 derivation.
        string trimmed = (newDaemonAddress ?? string.Empty).Trim();
        if (!Monero.DaemonAddress.IsValid(trimmed))
        {
            // Deliberately does NOT echo the typed value: exception messages end up in the log,
            // and the node address is privacy-sensitive metadata (or worse, a mis-pasted secret).
            throw new ArgumentException(
                "Daemon address must be a valid http(s) URL, e.g. http://127.0.0.1:18081.", nameof(newDaemonAddress));
        }

        Opened? hit = OpenWithPolicy(password);
        if (hit is null)
        {
            return false;
        }

        using (hit.Slot)
        {
            hit.Secrets.DaemonAddress = trimmed;
            WriteSlotZeroing(_file, hit.Slot.SlotIndex, password, hit.Secrets);
            Persist();
            if (hit.Wiped)
            {
                ShredSiblingCopies();
            }

            return true;
        }
    }

    /// <summary>A slot opened by a password, after its on-open policy ran (in memory only).</summary>
    private sealed record Opened(OpenedSlot Slot, WalletSecrets Secrets, bool Wiped, bool Migrated);

    /// <summary>
    /// Open whichever slot <paramref name="password"/> decrypts and apply its on-open policy to the
    /// in-memory file: wipe-on-duress (randomise the other slot and clear the flag, so the vault now
    /// looks like a single-wallet vault) and v1→v2 payload migration. Both re-seal the opened slot
    /// with the key already derived, so no extra Argon2 work happens. The caller persists.
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
            WalletSecrets secrets = SlotPayload.Deserialize(slot.Plaintext.Span, out bool legacy);

            bool wiped = false;
            if (secrets.WipeOtherSlotOnUnlock)
            {
                _file.FillRandom(OtherSlot(slot.SlotIndex));
                secrets.WipeOtherSlotOnUnlock = false; // consumed
                wiped = true;
            }

            if (wiped || legacy)
            {
                byte[] payload = SlotPayload.Serialize(secrets);
                try
                {
                    _file.ResealSlot(slot, payload);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }

            return new Opened(slot, secrets, wiped, legacy);
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
            Persist();
        }
        catch
        {
            try { Persist(); } catch { /* second attempt; then give up silently */ }
        }
    }

    /// <summary>
    /// After a duress wipe, also destroy earlier copies of this vault that the app itself left
    /// beside it (Settings → Restore keeps the replaced vault as "vault.xv.replaced-*"). Those
    /// copies may still hold the wallet the wipe just destroyed — "wipe the real wallet on this
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

    private void Persist()
    {
        byte[] data = _file.Serialize();
        string tmp = _path + ".tmp";

        try
        {
            // Write + flush to disk so the bytes are durable before we swap the file in.
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
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
    /// Serialize the secrets, hand them to the slot, then ZERO the serialized JSON — it holds the
    /// mnemonic in cleartext, and leaving it for the GC would undercut the SecureBuffer discipline
    /// used everywhere else. (The slot sealing zeroes its own padded copy.)
    /// </summary>
    private static void WriteSlotZeroing(VaultFile file, int slotIndex, SecureBuffer password, WalletSecrets secrets)
    {
        byte[] payload = SlotPayload.Serialize(secrets);
        try
        {
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

    /// <summary>Whitespace- and case-insensitive mnemonic comparison.</summary>
    private static bool MnemonicsEqual(string? a, string? b)
    {
        static string Norm(string? m) => string.Join(' ',
            (m ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return Norm(a) == Norm(b);
    }
}
