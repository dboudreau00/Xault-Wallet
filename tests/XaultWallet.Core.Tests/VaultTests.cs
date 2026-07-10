using XaultWallet.Core.Models;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

public class VaultCryptoTests
{
    // Fast Argon2 params so the test suite stays quick. Never use these in production.
    private static readonly VaultCrypto.Argon2Parameters FastArgon = new(MemoryKib: 19_456, Iterations: 2, Parallelism: 1);

    [Fact]
    public void Encrypt_Then_Decrypt_RoundTrips()
    {
        using var pw = SecureBuffer.FromPassword("correct horse battery".ToCharArray());
        byte[] salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSizeBytes);
        using var key = VaultCrypto.DeriveKey(pw, salt, FastArgon);

        byte[] plaintext = "top secret seed"u8.ToArray();
        byte[] blob = VaultCrypto.Encrypt(key, plaintext, ReadOnlySpan<byte>.Empty);

        using var dec = VaultCrypto.TryDecrypt(key, blob, ReadOnlySpan<byte>.Empty);
        Assert.NotNull(dec);
        Assert.Equal(plaintext, dec!.Span.ToArray());
    }

    [Fact]
    public void Wrong_Key_Fails_Auth_And_Returns_Null()
    {
        byte[] salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSizeBytes);
        using var right = SecureBuffer.FromPassword("right".ToCharArray());
        using var wrong = SecureBuffer.FromPassword("wrong".ToCharArray());
        using var rightKey = VaultCrypto.DeriveKey(right, salt, FastArgon);
        using var wrongKey = VaultCrypto.DeriveKey(wrong, salt, FastArgon);

        byte[] blob = VaultCrypto.Encrypt(rightKey, "data"u8, ReadOnlySpan<byte>.Empty);
        Assert.Null(VaultCrypto.TryDecrypt(wrongKey, blob, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Tampered_Ciphertext_Is_Rejected()
    {
        byte[] salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSizeBytes);
        using var pw = SecureBuffer.FromPassword("pw".ToCharArray());
        using var key = VaultCrypto.DeriveKey(pw, salt, FastArgon);

        byte[] blob = VaultCrypto.Encrypt(key, "data"u8, ReadOnlySpan<byte>.Empty);
        blob[^1] ^= 0xFF; // flip a bit in the last ciphertext byte
        Assert.Null(VaultCrypto.TryDecrypt(key, blob, ReadOnlySpan<byte>.Empty));
    }
}

public class VaultManagerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"xvtest_{Guid.NewGuid():N}.xv");
    private static readonly VaultCrypto.Argon2Parameters FastArgon = new(19_456, 2, 1);

    private static SecureBuffer Pw(string s) => SecureBuffer.FromPassword(s.ToCharArray());

    [Fact]
    public void Main_Password_Opens_Real_Wallet()
    {
        var main = new WalletSecrets { Label = "Main", Mnemonic = "seed words real" };
        using (var mp = Pw("main-pass-123"))
        {
            VaultManager.Create(_path, mp, main, argon: FastArgon);
        }

        var mgr = VaultManager.Load(_path);
        using var mp2 = Pw("main-pass-123");
        UnlockResult? r = mgr.Unlock(mp2);

        Assert.NotNull(r);
        Assert.False(r!.WasDuress);
        Assert.Equal("seed words real", r.Secrets.Mnemonic);
    }

    [Fact]
    public void Duress_Password_Opens_Decoy_And_Flags_Duress()
    {
        var main = new WalletSecrets { Mnemonic = "real seed" };
        var decoy = new WalletSecrets { Mnemonic = "decoy seed" };

        using (var mp = Pw("main-pass-123"))
        using (var dp = Pw("duress-pass-456"))
        {
            VaultManager.Create(_path, mp, main, dp, decoy, FastArgon);
        }

        var mgr = VaultManager.Load(_path);
        using var dp2 = Pw("duress-pass-456");
        UnlockResult? r = mgr.Unlock(dp2);

        Assert.NotNull(r);
        Assert.True(r!.WasDuress);
        Assert.Equal("decoy seed", r.Secrets.Mnemonic);
    }

    [Fact]
    public void Wrong_Password_Returns_Null()
    {
        var main = new WalletSecrets { Mnemonic = "real seed" };
        using (var mp = Pw("main-pass-123"))
        {
            VaultManager.Create(_path, mp, main, argon: FastArgon);
        }

        var mgr = VaultManager.Load(_path);
        using var bad = Pw("not-the-password");
        Assert.Null(mgr.Unlock(bad));
    }

    [Fact]
    public void WipeReal_Destroys_Real_Slot_After_Duress_Unlock()
    {
        var main = new WalletSecrets { Mnemonic = "real seed" };
        var decoy = new WalletSecrets { Mnemonic = "decoy seed", DuressWipeReal = true };

        using (var mp = Pw("main-pass-123"))
        using (var dp = Pw("duress-pass-456"))
        {
            VaultManager.Create(_path, mp, main, dp, decoy, FastArgon);
        }

        // Open under duress; the wipe policy travels inside the encrypted decoy payload.
        var mgr = VaultManager.Load(_path);
        using (var dp2 = Pw("duress-pass-456"))
        {
            Assert.True(mgr.Unlock(dp2)!.WasDuress);
        }

        // Now the real password should no longer work.
        var reopened = VaultManager.Load(_path);
        using var mp3 = Pw("main-pass-123");
        Assert.Null(reopened.Unlock(mp3));

        // …but the duress password still does.
        using var dp3 = Pw("duress-pass-456");
        Assert.True(reopened.Unlock(dp3)!.WasDuress);
    }

    [Fact]
    public void ChangeMainPassword_Works_And_Rejects_Duress_Password()
    {
        var main = new WalletSecrets { Mnemonic = "real seed" };
        var decoy = new WalletSecrets { Mnemonic = "decoy seed" };
        using (var mp = Pw("old-main"))
        using (var dp = Pw("duress-x"))
        {
            VaultManager.Create(_path, mp, main, dp, decoy, FastArgon);
        }

        var mgr = VaultManager.Load(_path);

        // Duress password must not be usable to change the main password.
        using (var dp = Pw("duress-x"))
        using (var np = Pw("hacked"))
        {
            Assert.False(mgr.ChangeMainPassword(dp, np));
        }

        using (var mp = Pw("old-main"))
        using (var np = Pw("new-main"))
        {
            Assert.True(mgr.ChangeMainPassword(mp, np));
        }

        var reopened = VaultManager.Load(_path);
        using var newpw = Pw("new-main");
        Assert.Equal("real seed", reopened.Unlock(newpw)!.Secrets.Mnemonic);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
