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

        mainSecrets.Kind = ProfileKind.Real;
        var file = VaultFile.CreateEmpty(argon ?? VaultCrypto.Argon2Parameters.Default);

        // Randomise which physical slot holds the real wallet so position leaks nothing.
        int realSlot = VaultCrypto.RandomBytes(1)[0] % VaultFile.SlotCount;
        int otherSlot = realSlot == 0 ? 1 : 0;

        file.WriteSlot(realSlot, mainPassword, Serialize(mainSecrets));

        if (duressPassword is not null && duressSecrets is not null)
        {
            duressSecrets.Kind = ProfileKind.Duress;
            file.WriteSlot(otherSlot, duressPassword, Serialize(duressSecrets));
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
            Persist();
        }

        return new UnlockResult(secrets, wasDuress);
    }

    /// <summary>
    /// Re-encrypt the real wallet under a new password. Requires the current
    /// password to first recover and confirm the real slot.
    /// </summary>
    public bool ChangeMainPassword(SecureBuffer currentPassword, SecureBuffer newPassword)
    {
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

            _file.WriteSlot(slotIndex, newPassword, Serialize(secrets));
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
        if (!IsValidDaemonAddress(trimmed))
        {
            throw new ArgumentException(
                $"Daemon address must be a valid http(s) URL: '{newDaemonAddress}'.", nameof(newDaemonAddress));
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
            _file.WriteSlot(slotIndex, password, Serialize(secrets));
            Persist();
            return true;
        }
        finally
        {
            plaintext.Dispose();
        }
    }

    private static bool IsValidDaemonAddress(string address) =>
        !string.IsNullOrWhiteSpace(address)
        && Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

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
