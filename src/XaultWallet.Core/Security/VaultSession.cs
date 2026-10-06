using System.Security.Cryptography;
using XaultWallet.Core.Models;

namespace XaultWallet.Core.Security;

/// <summary>
/// An unlocked profile that stays open: the app reads and changes <see cref="Profile"/> (wallets
/// added, renamed or removed, contacts, subaddress labels, notes) and calls <see cref="Save"/>,
/// which re-seals ONLY this profile's slot with the key derived at unlock. No password prompt per
/// change, and no extra Argon2 work: holding the key while unlocked exposes nothing the decrypted
/// seeds in memory don't already.
///
/// Saving re-reads the vault from disk and replaces only this slot, so the other slot — whatever it
/// is — is written back byte for byte. Dispose on lock: that zeroes the key.
/// </summary>
public sealed class VaultSession : IDisposable
{
    private readonly VaultManager _vault;
    private readonly int _slotIndex;
    private readonly object _gate = new();
    private SecureBuffer _key;
    private byte[] _salt;
    private bool _disposed;

    internal VaultSession(VaultManager vault, OpenedSlot opened, WalletProfile profile, bool upgradedFrom01)
    {
        _vault = vault;
        _slotIndex = opened.SlotIndex;
        Profile = profile;
        UpgradedFromLegacyFormat = upgradedFrom01;

        // Keep a copy of the key and salt; the opened slot (and its decrypted JSON) goes now.
        _key = new SecureBuffer(opened.Key.Span);
        _salt = (byte[])opened.Salt.Clone();
        opened.Dispose();
    }

    /// <summary>The open profile. Change it, then <see cref="Save"/>.</summary>
    public WalletProfile Profile { get; }

    /// <summary>See <see cref="UnlockResult.UpgradedFromLegacyFormat"/>.</summary>
    public bool UpgradedFromLegacyFormat { get; }

    /// <summary>Write <see cref="Profile"/> back to its slot.</summary>
    /// <exception cref="VaultFullException">The profile no longer fits; nothing was written.</exception>
    /// <exception cref="IOException">The vault couldn't be written (or was replaced meanwhile).</exception>
    public void Save()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _vault.SaveSlot(_slotIndex, _key, _salt, Profile);
        }
    }

    /// <summary>
    /// <see cref="Save"/> for a UI: <see cref="Profile"/> is serialized NOW, on the calling thread
    /// (the one that changes it, so the snapshot can't race a change), and the encryption and the
    /// write run in the background. Several calls write in the order they finish; each writes the
    /// whole profile as it was when called.
    /// </summary>
    public Task SaveAsync()
    {
        byte[] payload;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            payload = SlotPayload.Serialize(Profile);
        }

        return Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _vault.SaveSlotPayload(_slotIndex, _key, _salt, payload);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        });
    }

    /// <summary>True when <paramref name="password"/> is THIS profile's password (for confirming
    /// something destructive or revealing, like removing a wallet or showing its seed).</summary>
    public bool CheckPassword(SecureBuffer password)
    {
        ArgumentNullException.ThrowIfNull(password);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _vault.OpensSlot(password, _slotIndex);
        }
    }

    /// <summary>
    /// Change THIS profile's password. False when <paramref name="currentPassword"/> isn't this
    /// profile's password — including when it is the OTHER slot's: an open session only ever changes
    /// its own slot (the same rule for both, so the rule itself reveals nothing). Changing the other
    /// slot from here could fire its wipe-on-duress under an open profile.
    /// </summary>
    /// <exception cref="ArgumentException">The new password is empty, or would also open the other slot.</exception>
    public bool ChangePassword(SecureBuffer currentPassword, SecureBuffer newPassword)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (SecureBuffer key, byte[] salt)? next = _vault.ChangeSlotPassword(_slotIndex, currentPassword, newPassword, Profile);
            if (next is null)
            {
                return false;
            }

            _key.Dispose();
            _key = next.Value.key;
            _salt = next.Value.salt;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _key.Dispose();
            CryptographicOperations.ZeroMemory(_salt);
        }
    }
}
