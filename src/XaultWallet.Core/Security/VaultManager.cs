using System.Text.Json;
using XaultWallet.Core.Models;

namespace XaultWallet.Core.Security;

/// <summary>Outcome of a successful unlock.</summary>
public sealed record UnlockResult(WalletSecrets Secrets, bool WasDuress);

/// <summary>
/// Coordinates the vault file, the two slots, and the duress policy. This class
/// never compares passwords in plaintext: it just asks <see cref="VaultFile"/> to
/// try decrypting each slot and reads the <see cref="ProfileKind"/> out of whatever
/// decrypts successfully.
/// </summary>
public sealed class VaultManager
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private VaultFile _file;

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
        if (Exists(path))
        {
            throw new IOException($"A vault already exists at {path}.");
        }

        if (mainPassword.Length == 0)
        {
            throw new ArgumentException("The main password must not be empty.", nameof(mainPassword));
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

        mainSecrets.Kind = ProfileKind.Real;
        var file = VaultFile.CreateEmpty(argon ?? VaultCrypto.Argon2Parameters.Default);

        // Randomise which physical slot holds the real wallet so position leaks nothing.
        int realSlot = VaultCrypto.RandomBytes(1)[0] % VaultFile.SlotCount;
        int otherSlot = realSlot == 0 ? 1 : 0;

        WriteSlotZeroing(file, realSlot, mainPassword, mainSecrets);

        if (duressPassword is not null && duressSecrets is not null)
        {
            duressSecrets.Kind = ProfileKind.Duress;
            WriteSlotZeroing(file, otherSlot, duressPassword, duressSecrets);
        }
        // else: otherSlot keeps its random filler from CreateEmpty.

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
    /// Try to unlock with the given password. Returns null on a wrong password.
    /// On success, tells you whether the DURESS profile was opened, and applies
    /// the wipe policy stored inside the decrypted decoy payload (e.g. wiping the real slot).
    /// The caller must not reveal to the user which happened.
    /// </summary>
    public UnlockResult? Unlock(SecureBuffer password)
    {
        var hit = _file.TryUnlock(password);
        if (hit is null)
        {
            return null;
        }

        (SecureBuffer plaintext, int slotIndex) = hit.Value;
        WalletSecrets secrets;
        try
        {
            secrets = Deserialize(plaintext.Span);
        }
        finally
        {
            plaintext.Dispose();
        }

        bool wasDuress = secrets.Kind == ProfileKind.Duress;

        if (wasDuress && secrets.DuressWipeReal)
        {
            // Overwrite whichever slot is NOT the one we just opened, then persist.
            int realSlot = slotIndex == 0 ? 1 : 0;
            _file.FillRandom(realSlot);

            // The wipe is best-effort: if the disk write fails (file locked by AV/backup
            // software, read-only media, disk full), the decoy must STILL open normally.
            // Surfacing an error here would make a duress unlock visibly different from a
            // normal one — at exactly the moment indistinguishability matters most.
            // Deliberately not logged either: a distinctive log line timestamped at the
            // duress unlock would tell the same story to anyone reading the log.
            try
            {
                Persist();
            }
            catch
            {
                try { Persist(); } catch { /* second attempt; then give up silently */ }
            }
        }

        return new UnlockResult(secrets, wasDuress);
    }

    /// <summary>
    /// Re-encrypt the real wallet under a new password. Requires the current
    /// password to first recover and confirm the real slot.
    /// </summary>
    public bool ChangeMainPassword(SecureBuffer currentPassword, SecureBuffer newPassword)
    {
        if (newPassword.Length == 0)
        {
            throw new ArgumentException("The new password must not be empty.", nameof(newPassword));
        }

        var hit = _file.TryUnlock(currentPassword);
        if (hit is null)
        {
            return false;
        }

        (SecureBuffer plaintext, int slotIndex) = hit.Value;
        try
        {
            var secrets = Deserialize(plaintext.Span);
            if (secrets.Kind != ProfileKind.Real)
            {
                return false; // Do not allow changing the main password via the duress password.
            }

            // Refuse a new password that also opens the OTHER slot: that would silently
            // recreate the ambiguous-unlock hazard Create guards against (one password
            // matching both slots — potentially triggering wipe-on-duress on a normal
            // unlock). The message stays neutral so it never confirms a second wallet.
            var collision = _file.TryUnlock(newPassword);
            if (collision is not null)
            {
                (SecureBuffer otherPlain, int otherSlot) = collision.Value;
                otherPlain.Dispose();
                if (otherSlot != slotIndex)
                {
                    throw new ArgumentException(
                        "That new password can't be used for this vault. Choose a different one.",
                        nameof(newPassword));
                }
            }

            WriteSlotZeroing(_file, slotIndex, newPassword, secrets);
            Persist();
            return true;
        }
        finally
        {
            plaintext.Dispose();
        }
    }

    /// <summary>
    /// Re-point the wallet that <paramref name="password"/> opens at a new daemon (node) address,
    /// then re-seal that same slot. Unlike <see cref="ChangeMainPassword"/>, this deliberately
    /// works for WHICHEVER slot the password unlocks — real OR duress — so the operation reveals
    /// nothing about which profile is which: a coercer watching cannot tell a real-wallet repoint
    /// from a decoy repoint. Changing a node is not a "master" action; each profile's own owner may
    /// legitimately repoint it. The other slot's bytes are left untouched.
    /// Returns false on a wrong password. Throws <see cref="ArgumentException"/> if the address is
    /// not a valid http(s) URL.
    /// </summary>
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

        var hit = _file.TryUnlock(password);
        if (hit is null)
        {
            return false;
        }

        (SecureBuffer plaintext, int slotIndex) = hit.Value;
        try
        {
            var secrets = Deserialize(plaintext.Span);
            secrets.DaemonAddress = trimmed;
            WriteSlotZeroing(_file, slotIndex, password, secrets);
            Persist();
            return true;
        }
        finally
        {
            plaintext.Dispose();
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

    private static byte[] Serialize(WalletSecrets s) => JsonSerializer.SerializeToUtf8Bytes(s, JsonOpts);

    /// <summary>
    /// Serialize the secrets, hand them to the slot, then ZERO the serialized JSON — it holds the
    /// mnemonic in cleartext, and leaving it for the GC would undercut the SecureBuffer discipline
    /// used everywhere else. (WriteSlot already zeroes its own padded copy.)
    /// </summary>
    private static void WriteSlotZeroing(VaultFile file, int slotIndex, SecureBuffer password, WalletSecrets secrets)
    {
        byte[] payload = Serialize(secrets);
        try
        {
            file.WriteSlot(slotIndex, password, payload);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Constant-time password comparison (false for different lengths).</summary>
    private static bool PasswordsEqual(SecureBuffer a, SecureBuffer b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a.Span, b.Span);

    /// <summary>Whitespace- and case-insensitive mnemonic comparison.</summary>
    private static bool MnemonicsEqual(string? a, string? b)
    {
        static string Norm(string? m) => string.Join(' ',
            (m ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return Norm(a) == Norm(b);
    }

    private static WalletSecrets Deserialize(ReadOnlySpan<byte> data)
    {
        try
        {
            return JsonSerializer.Deserialize<WalletSecrets>(data, JsonOpts)
                   ?? throw new InvalidDataException("Corrupt secrets payload.");
        }
        catch (JsonException ex)
        {
            // The password was correct (GCM tag verified) but the payload didn't parse — most
            // likely a vault written by an incompatible newer version of the app.
            throw new InvalidDataException("This vault was created by a different version of XaultWallet.", ex);
        }
    }
}
