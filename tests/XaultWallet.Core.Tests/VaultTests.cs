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
        var main = new WalletSecrets { Mnemonic = "seed words real" };
        using (var mp = Pw("main-pass-123"))
        {
            VaultManager.Create(_path, mp, main, argon: FastArgon);
        }

        var mgr = VaultManager.Load(_path);
        using var mp2 = Pw("main-pass-123");
        UnlockResult? r = mgr.Unlock(mp2);

        Assert.NotNull(r);
        Assert.Equal("seed words real", r!.Secrets.Mnemonic);
    }

    [Fact]
    public void Duress_Password_Opens_Decoy()
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
        Assert.Equal("decoy seed", r!.Secrets.Mnemonic);
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
        var decoy = new WalletSecrets { Mnemonic = "decoy seed", WipeOtherSlotOnUnlock = true };

        using (var mp = Pw("main-pass-123"))
        using (var dp = Pw("duress-pass-456"))
        {
            VaultManager.Create(_path, mp, main, dp, decoy, FastArgon);
        }

        // Open under duress; the wipe policy travels inside the encrypted decoy payload.
        var mgr = VaultManager.Load(_path);
        using (var dp2 = Pw("duress-pass-456"))
        {
            Assert.Equal("decoy seed", mgr.Unlock(dp2)!.Secrets.Mnemonic);
        }

        // Now the real password should no longer work.
        var reopened = VaultManager.Load(_path);
        using var mp3 = Pw("main-pass-123");
        Assert.Null(reopened.Unlock(mp3));

        // …but the duress password still does.
        using var dp3 = Pw("duress-pass-456");
        Assert.Equal("decoy seed", reopened.Unlock(dp3)!.Secrets.Mnemonic);
    }

    [Fact]
    public void ChangePassword_Is_Symmetric_And_Only_Touches_The_Opened_Slot()
    {
        // Deniability: the duress password must be able to change ITS OWN password exactly like the
        // main one can. The old asymmetric rule ("only the real wallet's password works here")
        // told a coercer holding the duress password that a real wallet exists.
        var main = new WalletSecrets { Mnemonic = "real seed" };
        var decoy = new WalletSecrets { Mnemonic = "decoy seed" };
        using (var mp = Pw("old-main"))
        using (var dp = Pw("duress-x"))
        {
            VaultManager.Create(_path, mp, main, dp, decoy, FastArgon);
        }

        using (var dp = Pw("duress-x"))
        using (var np = Pw("new-duress"))
        {
            Assert.True(VaultManager.Load(_path).ChangePassword(dp, np));
        }

        using (var mp = Pw("old-main"))
        using (var np = Pw("new-main"))
        {
            Assert.True(VaultManager.Load(_path).ChangePassword(mp, np));
        }

        using (var oldDuress = Pw("duress-x"))
        {
            Assert.Null(VaultManager.Load(_path).Unlock(oldDuress));
        }

        using (var newDuress = Pw("new-duress"))
        {
            Assert.Equal("decoy seed", VaultManager.Load(_path).Unlock(newDuress)!.Secrets.Mnemonic);
        }

        using var newMain = Pw("new-main");
        Assert.Equal("real seed", VaultManager.Load(_path).Unlock(newMain)!.Secrets.Mnemonic);
    }

    [Fact]
    public void ChangePassword_Wrong_Current_Password_Returns_False()
    {
        using (var mp = Pw("main-pass-123"))
        {
            VaultManager.Create(_path, mp, new WalletSecrets { Mnemonic = "real seed" }, argon: FastArgon);
        }

        using var bad = Pw("not-the-password");
        using var np = Pw("whatever-new");
        Assert.False(VaultManager.Load(_path).ChangePassword(bad, np));
    }

    [Fact]
    public void Session_Saves_A_Changed_Node_And_Preserves_Everything_Else()
    {
        var main = new WalletSecrets
        {
            Mnemonic = "real seed",
            Network = MoneroNetwork.Stagenet,
            RestoreHeight = 12345,
            DaemonAddress = "http://127.0.0.1:38081",
        };
        using (var mp = Pw("main-pass-123"))
        {
            VaultManager.Create(_path, mp, main, argon: FastArgon);
        }

        using (var mp = Pw("main-pass-123"))
        using (VaultSession session = VaultManager.Load(_path).OpenSession(mp)!)
        {
            session.Profile.ActiveWallet!.DaemonAddress = "http://node.example:38089";
            session.Save();
        }

        using var mp2 = Pw("main-pass-123");
        WalletSecrets s = VaultManager.Load(_path).Unlock(mp2)!.Secrets;
        Assert.Equal("http://node.example:38089", s.DaemonAddress);
        // Everything else must survive the re-seal untouched.
        Assert.Equal("real seed", s.Mnemonic);
        Assert.Equal(MoneroNetwork.Stagenet, s.Network);
        Assert.Equal(12345UL, s.RestoreHeight);
    }

    [Fact]
    public void Session_Opens_Only_With_The_Right_Password()
    {
        var main = new WalletSecrets { Mnemonic = "real seed", DaemonAddress = "http://a:1" };
        using (var mp = Pw("main-pass-123"))
        {
            VaultManager.Create(_path, mp, main, argon: FastArgon);
        }

        using var bad = Pw("not-the-password");
        Assert.Null(VaultManager.Load(_path).OpenSession(bad));
    }

    [Fact]
    public void Decoy_Session_Changes_Only_The_Decoy()
    {
        // Deniability: a change saved under the DURESS password touches only the decoy's slot and
        // leaves the real slot completely intact (and vice versa) — the operation reveals nothing.
        var main = new WalletSecrets { Mnemonic = "real seed", DaemonAddress = "http://real:1" };
        var decoy = new WalletSecrets { Mnemonic = "decoy seed", DaemonAddress = "http://decoy:1" };
        using (var mp = Pw("main-pass-123"))
        using (var dp = Pw("duress-pass-456"))
        {
            VaultManager.Create(_path, mp, main, dp, decoy, FastArgon);
        }

        using (var dp = Pw("duress-pass-456"))
        using (VaultSession session = VaultManager.Load(_path).OpenSession(dp)!)
        {
            session.Profile.ActiveWallet!.DaemonAddress = "http://decoy:2";
            session.Save();
        }

        var reopened = VaultManager.Load(_path);
        using (var dp = Pw("duress-pass-456"))
        {
            UnlockResult? r = reopened.Unlock(dp);
            Assert.Equal("http://decoy:2", r!.Secrets.DaemonAddress);
            Assert.Equal("decoy seed", r.Secrets.Mnemonic);
        }

        using (var mp = Pw("main-pass-123"))
        {
            UnlockResult? r = reopened.Unlock(mp);
            Assert.Equal("http://real:1", r!.Secrets.DaemonAddress);
            Assert.Equal("real seed", r.Secrets.Mnemonic);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
