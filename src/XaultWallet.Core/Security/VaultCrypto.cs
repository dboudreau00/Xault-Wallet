using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace XaultWallet.Core.Security;

/// <summary>
/// Low-level cryptographic primitives for the vault.
///
///   * Key derivation:  Argon2id  (memory-hard, resists GPU/ASIC brute force)
///   * Encryption:      AES-256-GCM (authenticated; the 128-bit tag both
///                      guarantees integrity AND acts as the "is this the
///                      right password?" check — no plaintext comparison
///                      of passwords ever happens anywhere in this app).
/// </summary>
public static class VaultCrypto
{
    public const int KeySizeBytes = 32;   // AES-256
    public const int SaltSizeBytes = 16;
    public const int NonceSizeBytes = 12; // 96-bit nonce, the GCM standard
    public const int TagSizeBytes = 16;   // 128-bit auth tag

    /// <summary>
    /// Argon2id parameters. These are deliberately on the strong side for a
    /// desktop app that unlocks infrequently. Tune <see cref="MemoryKib"/> down
    /// if you must support very low-memory machines, but never below 19 MiB.
    /// </summary>
    public sealed record Argon2Parameters(int MemoryKib, int Iterations, int Parallelism)
    {
        public static readonly Argon2Parameters Default = new(MemoryKib: 262_144 /* 256 MiB */, Iterations: 4, Parallelism: 4);

        // Upper bounds guard against a corrupted or malicious vault header trying to force a
        // multi-terabyte allocation (memory-exhaustion DoS) or an unbounded CPU spin on unlock.
        private const int MaxMemoryKib = 4 * 1024 * 1024; // 4 GiB
        private const int MaxIterations = 64;
        private const int MaxParallelism = 64;

        public void Validate()
        {
            if (MemoryKib < 19_456 || MemoryKib > MaxMemoryKib)
            {
                throw new ArgumentOutOfRangeException(nameof(MemoryKib), $"Argon2 memory must be 19 MiB..{MaxMemoryKib / 1024} MiB.");
            }

            if (Iterations < 2 || Iterations > MaxIterations)
            {
                throw new ArgumentOutOfRangeException(nameof(Iterations), $"Argon2 iterations must be 2..{MaxIterations}.");
            }

            if (Parallelism < 1 || Parallelism > MaxParallelism)
            {
                throw new ArgumentOutOfRangeException(nameof(Parallelism), $"Argon2 parallelism must be 1..{MaxParallelism}.");
            }
        }
    }

    /// <summary>Derive a 256-bit key from a password + salt using Argon2id.</summary>
    public static SecureBuffer DeriveKey(SecureBuffer password, ReadOnlySpan<byte> salt, Argon2Parameters p)
    {
        p.Validate();

        // Argon2id takes a byte[]; we cannot zero Konscious's internal copy, but we can zero ours.
        byte[] pwCopy = password.ToArray();
        byte[] saltCopy = salt.ToArray();
        byte[]? raw = null;
        try
        {
            using var argon2 = new Argon2id(pwCopy)
            {
                Salt = saltCopy,
                DegreeOfParallelism = p.Parallelism,
                MemorySize = p.MemoryKib,
                Iterations = p.Iterations,
            };

            raw = argon2.GetBytes(KeySizeBytes);
            return new SecureBuffer(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwCopy);
            if (raw is not null)
            {
                CryptographicOperations.ZeroMemory(raw);
            }
        }
    }

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> under <paramref name="key"/>.
    /// Output layout: [nonce (12)] [tag (16)] [ciphertext (== plaintext length)].
    /// </summary>
    public static byte[] Encrypt(SecureBuffer key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException("Key must be 32 bytes.", nameof(key));
        }

        Span<byte> nonce = stackalloc byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        byte[] output = new byte[NonceSizeBytes + TagSizeBytes + plaintext.Length];
        Span<byte> nonceDst = output.AsSpan(0, NonceSizeBytes);
        Span<byte> tagDst = output.AsSpan(NonceSizeBytes, TagSizeBytes);
        Span<byte> ctDst = output.AsSpan(NonceSizeBytes + TagSizeBytes);
        nonce.CopyTo(nonceDst);

        using var aes = new AesGcm(key.Span, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ctDst, tagDst, associatedData);
        return output;
    }

    /// <summary>
    /// Attempt to decrypt a blob produced by <see cref="Encrypt"/>.
    /// Returns null if the auth tag does not verify — which is exactly the
    /// signal for "wrong password / tampered data". Never throws on bad input.
    /// </summary>
    public static SecureBuffer? TryDecrypt(SecureBuffer key, ReadOnlySpan<byte> blob, ReadOnlySpan<byte> associatedData)
    {
        if (key.Length != KeySizeBytes || blob.Length < NonceSizeBytes + TagSizeBytes)
        {
            return null;
        }

        ReadOnlySpan<byte> nonce = blob.Slice(0, NonceSizeBytes);
        ReadOnlySpan<byte> tag = blob.Slice(NonceSizeBytes, TagSizeBytes);
        ReadOnlySpan<byte> ct = blob.Slice(NonceSizeBytes + TagSizeBytes);

        var plaintext = new SecureBuffer(ct.Length);
        try
        {
            using var aes = new AesGcm(key.Span, TagSizeBytes);
            aes.Decrypt(nonce, ct, tag, plaintext.Span, associatedData);
            return plaintext;
        }
        catch (Exception ex) when (ex is AuthenticationTagMismatchException or CryptographicException or ArgumentException)
        {
            // Wrong password, tampered data, or malformed lengths — all indistinguishable to a caller.
            plaintext.Dispose();
            return null;
        }
    }

    public static byte[] RandomBytes(int count)
    {
        byte[] b = new byte[count];
        RandomNumberGenerator.Fill(b);
        return b;
    }
}
