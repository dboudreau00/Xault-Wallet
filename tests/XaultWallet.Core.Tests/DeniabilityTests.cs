using System.Text;
using System.Text.Json.Nodes;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// The duress feature is only as good as what an examiner can learn from the vault file plus the
/// duress password — exactly what a coercer holds. These tests decrypt slots the way such an
/// examiner would (format is public) and assert there is nothing to find.
/// </summary>
public class DeniabilityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"xvtest_{Guid.NewGuid():N}.xv");
    private static readonly VaultCrypto.Argon2Parameters FastArgon = new(19_456, 2, 1);

    // Header (magic4 + ver1 + slots1 + reserved2 + 3×uint32) and slot (salt + nonce + tag + padded payload).
    private const int HeaderBytes = 20;
    private const int SlotBytes = VaultCrypto.SaltSizeBytes + VaultCrypto.NonceSizeBytes + VaultCrypto.TagSizeBytes + VaultFile.PaddedPlaintextBytes;

    private static SecureBuffer Pw(string s) => SecureBuffer.FromPassword(s.ToCharArray());

    private static WalletSecrets Secrets(string mnemonic, bool wipe = false) => new()
    {
        Network = MoneroNetwork.Stagenet,
        Mnemonic = mnemonic,
        RestoreHeight = 1234,
        DaemonAddress = "http://127.0.0.1:38081",
        EphemeralWalletPassword = Convert.ToHexString(VaultCrypto.RandomBytes(24)),
        WipeOtherSlotOnUnlock = wipe,
    };

    private void CreateVault(bool withDecoy, bool decoyWipes = false)
    {
        using var mp = Pw("main-password-123");
        if (!withDecoy)
        {
            VaultManager.Create(_path, mp, Secrets("real seed words"), argon: FastArgon);
            return;
        }

        using var dp = Pw("duress-password-456");
        VaultManager.Create(_path, mp, Secrets("real seed words"), dp, Secrets("decoy seed words", decoyWipes), FastArgon);
    }

    /// <summary>What an examiner sees: the raw JSON of whichever slot the password decrypts.</summary>
    private (JsonObject json, int slot) Examine(string password)
    {
        VaultFile file = VaultFile.Deserialize(File.ReadAllBytes(_path));
        using var pw = Pw(password);
        var hit = file.TryUnlock(pw) ?? throw new Xunit.Sdk.XunitException("password opened nothing");
        try
        {
            return (JsonNode.Parse(Encoding.UTF8.GetString(hit.plaintext.Span))!.AsObject(), hit.slotIndex);
        }
        finally
        {
            hit.plaintext.Dispose();
        }
    }

    private static string Shape(JsonObject o) => string.Join(",", o.Select(kv => kv.Key));

    [Fact]
    public void Decoy_Payload_Has_Exactly_The_Shape_Of_A_Single_Wallet_Payload()
    {
        CreateVault(withDecoy: false);
        string singleWalletShape = Shape(Examine("main-password-123").json);
        File.Delete(_path);

        CreateVault(withDecoy: true);
        JsonObject real = Examine("main-password-123").json;
        JsonObject decoy = Examine("duress-password-456").json;

        Assert.Equal(singleWalletShape, Shape(real));
        Assert.Equal(singleWalletShape, Shape(decoy));
        foreach (JsonObject o in new[] { real, decoy })
        {
            Assert.False(o.ContainsKey("kind"));
            Assert.False(o.ContainsKey("label"));
            Assert.False(o.ContainsKey("duressWipeReal"));
            Assert.Equal(2, (int)o["v"]!);
            Assert.False((bool)o["wipeOther"]!);
        }
    }

    [Fact]
    public void Wipe_Flag_Is_Consumed_So_The_Survivor_Looks_Like_A_Single_Wallet()
    {
        CreateVault(withDecoy: true, decoyWipes: true);

        // Documented exception: BEFORE the wipe fires, an offline examiner can see the flag.
        Assert.True((bool)Examine("duress-password-456").json["wipeOther"]!);

        using (var dp = Pw("duress-password-456"))
        {
            Assert.Equal("decoy seed words", VaultManager.Load(_path).Unlock(dp)!.Secrets.Mnemonic);
        }

        JsonObject after = Examine("duress-password-456").json;
        Assert.False((bool)after["wipeOther"]!);

        using var mp = Pw("main-password-123");
        Assert.Null(VaultManager.Load(_path).Unlock(mp));
    }

    [Fact]
    public void Wipe_Fires_When_The_Duress_Password_Is_Used_To_Change_A_Password()
    {
        CreateVault(withDecoy: true, decoyWipes: true);
        using (var dp = Pw("duress-password-456"))
        using (var np = Pw("coercer-chosen-pw"))
        {
            Assert.True(VaultManager.Load(_path).ChangePassword(dp, np));
        }

        using (var mp = Pw("main-password-123"))
        {
            Assert.Null(VaultManager.Load(_path).Unlock(mp));
        }

        Assert.False((bool)Examine("coercer-chosen-pw").json["wipeOther"]!);
    }

    [Fact]
    public void Wipe_Fires_When_The_Duress_Password_Is_Used_To_Change_The_Node()
    {
        CreateVault(withDecoy: true, decoyWipes: true);
        using (var dp = Pw("duress-password-456"))
        {
            Assert.True(VaultManager.Load(_path).ChangeDaemonAddress(dp, "http://node.example:38089"));
        }

        using var mp = Pw("main-password-123");
        Assert.Null(VaultManager.Load(_path).Unlock(mp));
    }

    [Theory]
    [InlineData("main-password-123")]
    [InlineData("duress-password-456")]
    public void An_Unlock_Without_A_Pending_Wipe_Never_Writes_The_Vault(string password)
    {
        CreateVault(withDecoy: true);
        byte[] before = File.ReadAllBytes(_path);
        using (var pw = Pw(password))
        {
            Assert.NotNull(VaultManager.Load(_path).Unlock(pw));
        }

        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void Wipe_Reseals_With_The_Already_Derived_Key()
    {
        // A duress unlock must not take longer than a normal one: the post-wipe re-seal reuses the
        // key derived while opening (same salt) instead of running Argon2 again.
        CreateVault(withDecoy: true, decoyWipes: true);
        int decoySlot = Examine("duress-password-456").slot;
        byte[] saltBefore = File.ReadAllBytes(_path).AsSpan(HeaderBytes + decoySlot * SlotBytes, VaultCrypto.SaltSizeBytes).ToArray();

        using (var dp = Pw("duress-password-456"))
        {
            Assert.NotNull(VaultManager.Load(_path).Unlock(dp));
        }

        byte[] saltAfter = File.ReadAllBytes(_path).AsSpan(HeaderBytes + decoySlot * SlotBytes, VaultCrypto.SaltSizeBytes).ToArray();
        Assert.Equal(saltBefore, saltAfter);
    }

    [Fact]
    public void Duress_Wipe_Destroys_Replaced_Vault_Copies_Left_By_Restore()
    {
        CreateVault(withDecoy: true, decoyWipes: true);
        string copy = _path + ".replaced-20260101-000000";
        File.Copy(_path, copy);

        using (var dp = Pw("duress-password-456"))
        {
            Assert.NotNull(VaultManager.Load(_path).Unlock(dp));
        }

        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void Normal_Unlock_Leaves_Replaced_Vault_Copies_Alone()
    {
        CreateVault(withDecoy: true, decoyWipes: true);
        string copy = _path + ".replaced-20260101-000000";
        File.Copy(_path, copy);

        using (var mp = Pw("main-password-123"))
        {
            Assert.NotNull(VaultManager.Load(_path).Unlock(mp));
        }

        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void Create_Rejects_A_Main_Wallet_That_Would_Wipe_The_Decoy()
    {
        using var mp = Pw("main-password-123");
        Assert.Throws<ArgumentException>(() =>
            VaultManager.Create(_path, mp, Secrets("real seed words", wipe: true), argon: FastArgon));
        Assert.False(File.Exists(_path));
    }

    // ---------------- legacy (v1) vaults ----------------

    /// <summary>Byte-for-byte what v1 of the app sealed: field order of the old WalletSecrets.</summary>
    private static byte[] LegacyPayload(int kind, string label, string mnemonic, bool duressWipeReal) =>
        Encoding.UTF8.GetBytes(
            $"{{\"kind\":{kind},\"label\":\"{label}\",\"network\":1,\"mnemonic\":\"{mnemonic}\",\"seedOffset\":\"\"," +
            $"\"restoreHeight\":42,\"daemonAddress\":\"http://127.0.0.1:38081\",\"ephemeralWalletPassword\":\"ABCDEF\"," +
            $"\"duressWipeReal\":{(duressWipeReal ? "true" : "false")}}}");

    private void WriteLegacyVault(bool decoyWipes, bool realSlotWipeFlag = false)
    {
        var file = VaultFile.CreateEmpty(FastArgon);
        using var rp = Pw("main-password-123");
        using var dp = Pw("duress-password-456");
        file.WriteSlot(0, rp, LegacyPayload(0, "Main", "real seed words", realSlotWipeFlag));
        file.WriteSlot(1, dp, LegacyPayload(1, "Wallet", "decoy seed words", decoyWipes));
        File.WriteAllBytes(_path, file.Serialize());
    }

    [Fact]
    public void Legacy_Vault_Opens_And_The_Opened_Slot_Is_Migrated()
    {
        WriteLegacyVault(decoyWipes: false);

        using (var mp = Pw("main-password-123"))
        {
            WalletSecrets s = VaultManager.Load(_path).Unlock(mp)!.Secrets;
            Assert.Equal("real seed words", s.Mnemonic);
            Assert.Equal(MoneroNetwork.Stagenet, s.Network);
            Assert.Equal(42UL, s.RestoreHeight);
            Assert.Equal("ABCDEF", s.EphemeralWalletPassword);
        }

        JsonObject real = Examine("main-password-123").json;
        Assert.Equal(2, (int)real["v"]!);
        Assert.False(real.ContainsKey("kind"));
        Assert.False(real.ContainsKey("label"));

        // The decoy slot can only be migrated by its own password; until then it is untouched.
        Assert.True(Examine("duress-password-456").json.ContainsKey("kind"));
        using (var dp = Pw("duress-password-456"))
        {
            Assert.Equal("decoy seed words", VaultManager.Load(_path).Unlock(dp)!.Secrets.Mnemonic);
        }

        Assert.Equal(Shape(real), Shape(Examine("duress-password-456").json));
    }

    [Theory]
    [InlineData("main-password-123")]
    [InlineData("duress-password-456")]
    public void Upgrade_Is_Reported_Once_And_Identically_For_Either_Slot(string password)
    {
        // The notice must not single out one slot: a notice only for v1 "real" slots would make the
        // first decoy unlock after upgrading look different from the first real one.
        WriteLegacyVault(decoyWipes: false);

        using var pw = Pw(password);
        Assert.True(VaultManager.Load(_path).Unlock(pw)!.UpgradedFromLegacyFormat);
        Assert.False(VaultManager.Load(_path).Unlock(pw)!.UpgradedFromLegacyFormat); // already v2
    }

    [Fact]
    public void A_Current_Format_Vault_Never_Reports_An_Upgrade()
    {
        using (var mp = Pw("main-password-123"))
        {
            VaultManager.Create(_path, mp, Secrets("real seed words"), argon: FastArgon);
        }

        using var again = Pw("main-password-123");
        Assert.False(VaultManager.Load(_path).Unlock(again)!.UpgradedFromLegacyFormat);
    }

    [Fact]
    public void Legacy_Decoy_With_Wipe_Still_Wipes_And_Is_Migrated()
    {
        WriteLegacyVault(decoyWipes: true);

        using (var dp = Pw("duress-password-456"))
        {
            Assert.Equal("decoy seed words", VaultManager.Load(_path).Unlock(dp)!.Secrets.Mnemonic);
        }

        using (var mp = Pw("main-password-123"))
        {
            Assert.Null(VaultManager.Load(_path).Unlock(mp));
        }

        JsonObject decoy = Examine("duress-password-456").json;
        Assert.False(decoy.ContainsKey("kind"));
        Assert.False((bool)decoy["wipeOther"]!);
    }

    [Fact]
    public void Legacy_Real_Slot_Never_Wipes_Even_With_A_Stray_Flag()
    {
        // v1 honoured duressWipeReal ONLY on a duress-kind slot; migration must not change that.
        WriteLegacyVault(decoyWipes: false, realSlotWipeFlag: true);

        using (var mp = Pw("main-password-123"))
        {
            Assert.NotNull(VaultManager.Load(_path).Unlock(mp));
        }

        using var dp = Pw("duress-password-456");
        Assert.Equal("decoy seed words", VaultManager.Load(_path).Unlock(dp)!.Secrets.Mnemonic);
    }

    [Fact]
    public void Payload_From_A_Newer_App_Version_Is_Refused()
    {
        var file = VaultFile.CreateEmpty(FastArgon);
        using (var pw = Pw("main-password-123"))
        {
            file.WriteSlot(0, pw, Encoding.UTF8.GetBytes("{\"v\":3,\"mnemonic\":\"future\"}"));
        }

        File.WriteAllBytes(_path, file.Serialize());
        using var mp = Pw("main-password-123");
        Assert.Throws<InvalidDataException>(() => VaultManager.Load(_path).Unlock(mp));
    }

    // ---------------- VaultFile re-seal primitive ----------------

    [Fact]
    public void ResealSlot_Uses_A_Fresh_Nonce_And_Still_Opens()
    {
        var file = VaultFile.CreateEmpty(FastArgon);
        using (var pw = Pw("alpha-pass"))
        {
            file.WriteSlot(1, pw, "original"u8);
        }

        byte[] before = file.Serialize();
        using (var pw = Pw("alpha-pass"))
        using (OpenedSlot opened = file.TryOpen(pw)!)
        {
            Assert.Equal(1, opened.SlotIndex);
            file.ResealSlot(opened, "replaced"u8);
        }

        byte[] after = file.Serialize();
        int slot1 = HeaderBytes + SlotBytes;
        Assert.Equal(before.AsSpan(slot1, VaultCrypto.SaltSizeBytes).ToArray(), after.AsSpan(slot1, VaultCrypto.SaltSizeBytes).ToArray());
        Assert.NotEqual(
            before.AsSpan(slot1 + VaultCrypto.SaltSizeBytes, VaultCrypto.NonceSizeBytes).ToArray(),
            after.AsSpan(slot1 + VaultCrypto.SaltSizeBytes, VaultCrypto.NonceSizeBytes).ToArray());

        using var again = Pw("alpha-pass");
        var hit = VaultFile.Deserialize(after).TryUnlock(again);
        Assert.Equal("replaced", Encoding.UTF8.GetString(hit!.Value.plaintext.Span));
        hit.Value.plaintext.Dispose();
    }

    public void Dispose()
    {
        try
        {
            string dir = Path.GetDirectoryName(_path)!;
            foreach (string f in Directory.EnumerateFiles(dir, Path.GetFileName(_path) + "*"))
            {
                File.Delete(f);
            }
        }
        catch
        {
            // temp cleanup only
        }
    }
}
