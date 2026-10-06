using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// 0.5's vault: one profile (several wallets, an address book, labels, notes) per password, in
/// 256 KiB slots, changed through an open <see cref="VaultSession"/>. And the way vaults written by
/// 0.1–0.3 (4 KiB slots, file version 1) are carried over: each old slot is converted only when its
/// own password opens it, without disturbing — or revealing — the other.
/// </summary>
public sealed class MultiWalletVaultTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"xvtest_{Guid.NewGuid():N}.xv");
    private static readonly VaultCrypto.Argon2Parameters FastArgon = new(19_456, 2, 1);

    private const string MainPw = "main-password-123";
    private const string DuressPw = "duress-password-456";

    // Version-1 layout (0.1–0.3): 20-byte header, then two slots of salt|nonce|tag|4096-byte payload.
    private const int HeaderBytes = 20;
    private const int LegacySlotBytes = VaultCrypto.SaltSizeBytes + VaultCrypto.NonceSizeBytes + VaultCrypto.TagSizeBytes + 4096;
    private const int SlotBytes = VaultCrypto.SaltSizeBytes + VaultCrypto.NonceSizeBytes + VaultCrypto.TagSizeBytes + VaultFile.PaddedPlaintextBytes;

    private static SecureBuffer Pw(string s) => SecureBuffer.FromPassword(s.ToCharArray());

    private static WalletSecrets Wallet(string mnemonic, string name = WalletSecrets.DefaultName) => new()
    {
        Name = name,
        Network = MoneroNetwork.Stagenet,
        Mnemonic = mnemonic,
        RestoreHeight = 1234,
        DaemonAddress = "http://127.0.0.1:38081",
        EphemeralWalletPassword = Convert.ToHexString(VaultCrypto.RandomBytes(24)),
    };

    /// <summary>A 0.2/0.3 slot payload (v2), byte for byte as those versions sealed it.</summary>
    private static byte[] V2Payload(string mnemonic, bool wipeOther = false) => Encoding.UTF8.GetBytes(
        $"{{\"v\":2,\"network\":1,\"mnemonic\":\"{mnemonic}\",\"seedOffset\":\"\",\"restoreHeight\":42," +
        $"\"daemonAddress\":\"http://127.0.0.1:38081\",\"ephemeralWalletPassword\":\"ABCDEF\",\"wipeOther\":{(wipeOther ? "true" : "false")}}}");

    /// <summary>A 0.1 slot payload (v1), with its real/decoy marker.</summary>
    private static byte[] V1Payload(int kind, string mnemonic, bool duressWipeReal = false) => Encoding.UTF8.GetBytes(
        $"{{\"kind\":{kind},\"label\":\"{(kind == 0 ? "Main" : "Wallet")}\",\"network\":1,\"mnemonic\":\"{mnemonic}\",\"seedOffset\":\"\"," +
        $"\"restoreHeight\":42,\"daemonAddress\":\"http://127.0.0.1:38081\",\"ephemeralWalletPassword\":\"ABCDEF\"," +
        $"\"duressWipeReal\":{(duressWipeReal ? "true" : "false")}}}");

    /// <summary>
    /// A version-1 vault FILE exactly as 0.1–0.3 wrote it: "XVLT", version 1, two 4140-byte slots,
    /// each [salt][AES-GCM(nonce|tag|[len][payload][random pad to 4096])] with associated data
    /// "XV",1,slot. A null slot is random filler.
    /// </summary>
    private static byte[] LegacyFile((string password, byte[] payload)? slot0, (string password, byte[] payload)? slot1)
    {
        byte[] file = new byte[HeaderBytes + (2 * LegacySlotBytes)];
        "XVLT"u8.CopyTo(file);
        file[4] = 1;
        file[5] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)FastArgon.MemoryKib);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)FastArgon.Iterations);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)FastArgon.Parallelism);

        (string password, byte[] payload)?[] slots = [slot0, slot1];
        for (int i = 0; i < 2; i++)
        {
            Span<byte> target = file.AsSpan(HeaderBytes + (i * LegacySlotBytes), LegacySlotBytes);
            if (slots[i] is not { } s)
            {
                RandomNumberGenerator.Fill(target);
                continue;
            }

            byte[] salt = VaultCrypto.RandomBytes(VaultCrypto.SaltSizeBytes);
            using SecureBuffer pw = Pw(s.password);
            using SecureBuffer key = VaultCrypto.DeriveKey(pw, salt, FastArgon);
            byte[] padded = VaultCrypto.RandomBytes(4096);
            BinaryPrimitives.WriteUInt32LittleEndian(padded, (uint)s.payload.Length);
            s.payload.CopyTo(padded, 4);
            byte[] enc = VaultCrypto.Encrypt(key, padded, [(byte)'X', (byte)'V', 1, (byte)i]);
            salt.CopyTo(target);
            enc.CopyTo(target[VaultCrypto.SaltSizeBytes..]);
        }

        return file;
    }

    /// <summary>The decrypted JSON of whichever slot the password opens (what an examiner sees).</summary>
    private JsonObject Examine(string password)
    {
        VaultFile file = VaultFile.Deserialize(File.ReadAllBytes(_path));
        using var pw = Pw(password);
        var hit = file.TryUnlock(pw) ?? throw new Xunit.Sdk.XunitException("password opened nothing");
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(hit.plaintext.Span))!.AsObject();
        }
        finally
        {
            hit.plaintext.Dispose();
        }
    }

    private void CreateVault(bool withDecoy, bool decoyWipes = false)
    {
        using var mp = Pw(MainPw);
        if (!withDecoy)
        {
            VaultManager.Create(_path, mp, Wallet("real seed words"), argon: FastArgon);
            return;
        }

        using var dp = Pw(DuressPw);
        WalletSecrets decoy = Wallet("decoy seed words");
        decoy.WipeOtherSlotOnUnlock = decoyWipes;
        VaultManager.Create(_path, mp, Wallet("real seed words"), dp, decoy, FastArgon);
    }

    private VaultSession Open(string password)
    {
        using var pw = Pw(password);
        return VaultManager.Load(_path).OpenSession(pw) ?? throw new Xunit.Sdk.XunitException("password opened nothing");
    }

    // ------------------------------------------------------------ the format

    [Fact]
    public void New_Vaults_Are_Version_2_Files_Of_Constant_Size()
    {
        CreateVault(withDecoy: false);
        byte[] single = File.ReadAllBytes(_path);
        File.Delete(_path);
        CreateVault(withDecoy: true);
        byte[] both = File.ReadAllBytes(_path);

        Assert.Equal(VaultFile.FileBytes, single.Length);
        Assert.Equal(VaultFile.FileBytes, both.Length);
        Assert.Equal(2, single[4]);
        Assert.Equal(2, both[4]);
    }

    [Fact]
    public void Profile_Round_Trips_Wallets_Contacts_Labels_And_Notes()
    {
        CreateVault(withDecoy: false);
        string secondId;
        using (VaultSession s = Open(MainPw))
        {
            WalletSecrets first = s.Profile.Wallets[0];
            first.Name = "Savings";
            first.SubaddressCounts[0] = 4;
            first.Labels["0/3"] = "Alice";
            first.TxNotes["ab12"] = "rent";

            var view = new WalletSecrets
            {
                Name = "Cold storage",
                Kind = WalletKind.ViewOnly,
                Network = MoneroNetwork.Mainnet,
                Address = "4AdUndXHHZ6cfufTMvppY6JwXNouMBzSkbLYfpAV5Usx3skxNgYeYTRj5UzqtReoS44qo9mtmXCqY45DJ852K5Jv2684Rge",
                ViewKey = new string('a', 64),
                RestoreHeight = 3_000_000,
                DaemonAddress = "http://node.example:18089",
            };
            s.Profile.Wallets.Add(view);
            secondId = view.Id;
            s.Profile.ActiveWalletId = view.Id;
            s.Profile.Contacts.Add(new Contact { Name = "Bob", Address = "4Bob…", Note = "landlord" });
            s.Save();
        }

        using VaultSession again = Open(MainPw);
        WalletProfile p = again.Profile;
        Assert.Equal(2, p.Wallets.Count);
        Assert.Equal("Savings", p.Wallets[0].Name);
        Assert.Equal(4u, p.Wallets[0].SubaddressCounts[0]);
        Assert.Equal("Alice", p.Wallets[0].Labels["0/3"]);
        Assert.Equal("rent", p.Wallets[0].TxNotes["ab12"]);
        Assert.Equal("real seed words", p.Wallets[0].Mnemonic);

        WalletSecrets cold = p.ActiveWallet!;
        Assert.Equal(secondId, cold.Id);
        Assert.Equal(WalletKind.ViewOnly, cold.Kind);
        Assert.False(cold.CanSpend);
        Assert.Equal(new string('a', 64), cold.ViewKey);
        Assert.Equal(3_000_000UL, cold.RestoreHeight);
        Assert.Equal("Bob", Assert.Single(p.Contacts).Name);
        Assert.Equal("landlord", p.Contacts[0].Note);
    }

    [Fact]
    public void Both_Profiles_Have_The_Same_Shape_And_No_Real_Or_Decoy_Marker()
    {
        CreateVault(withDecoy: true);
        JsonObject real = Examine(MainPw);
        JsonObject decoy = Examine(DuressPw);

        static string Shape(JsonObject o) => string.Join(",", o.Select(kv => kv.Key));
        static string WalletShape(JsonObject o) => string.Join(",", o["wallets"]!.AsArray()[0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(Shape(real), Shape(decoy));
        Assert.Equal(WalletShape(real), WalletShape(decoy));
        Assert.Equal(WalletSecrets.DefaultName, (string?)real["wallets"]![0]!["name"]);
        Assert.Equal(WalletSecrets.DefaultName, (string?)decoy["wallets"]![0]!["name"]);
        foreach (JsonObject o in new[] { real, decoy })
        {
            Assert.Equal(3, (int)o["v"]!);
            Assert.False(o.ContainsKey("kind"));
            Assert.False(o["wallets"]![0]!.AsObject().ContainsKey("kind")); // the restore type is "type"
        }
    }

    [Fact]
    public void Save_Writes_Only_Its_Own_Slot()
    {
        CreateVault(withDecoy: true);
        byte[] before = File.ReadAllBytes(_path);
        int decoySlot;
        using (VaultSession s = Open(DuressPw))
        {
            s.Profile.Contacts.Add(new Contact { Name = "Carol", Address = "4Carol…" });
            s.Save();
        }

        byte[] after = File.ReadAllBytes(_path);
        decoySlot = before.AsSpan(HeaderBytes, SlotBytes).SequenceEqual(after.AsSpan(HeaderBytes, SlotBytes)) ? 1 : 0;
        int realSlot = 1 - decoySlot;

        Assert.Equal(VaultFile.FileBytes, after.Length);
        Assert.True(before.AsSpan(HeaderBytes + (realSlot * SlotBytes), SlotBytes).SequenceEqual(after.AsSpan(HeaderBytes + (realSlot * SlotBytes), SlotBytes)));
        Assert.False(before.AsSpan(HeaderBytes + (decoySlot * SlotBytes), SlotBytes).SequenceEqual(after.AsSpan(HeaderBytes + (decoySlot * SlotBytes), SlotBytes)));
        Assert.Equal("Carol", (string?)Examine(DuressPw)["contacts"]![0]!["name"]);
        Assert.Empty(Examine(MainPw)["contacts"]!.AsArray());
    }

    [Fact]
    public void A_Full_Vault_Is_Refused_And_Nothing_Is_Written()
    {
        CreateVault(withDecoy: false);
        byte[] before = File.ReadAllBytes(_path);
        using VaultSession s = Open(MainPw);
        for (int i = 0; i < 400; i++)
        {
            s.Profile.Wallets[0].TxNotes[$"tx{i:D4}"] = new string('n', 1000);
        }

        Assert.Throws<VaultFullException>(s.Save);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void A_Profile_Without_Wallets_Is_Not_A_Valid_Vault()
    {
        var file = VaultFile.CreateEmpty(FastArgon);
        using (var pw = Pw(MainPw))
        {
            file.WriteSlot(0, pw, Encoding.UTF8.GetBytes("{\"v\":3,\"wallets\":[],\"contacts\":[]}"));
        }

        File.WriteAllBytes(_path, file.Serialize());
        using var mp = Pw(MainPw);
        Assert.Throws<InvalidDataException>(() => VaultManager.Load(_path).Unlock(mp));
    }

    [Fact]
    public void A_File_From_A_Newer_Version_Is_Refused_Plainly()
    {
        CreateVault(withDecoy: false);
        byte[] bytes = File.ReadAllBytes(_path);
        bytes[4] = 3;
        File.WriteAllBytes(_path, bytes);

        var ex = Assert.Throws<InvalidDataException>(() => VaultManager.Load(_path));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void Create_Refuses_A_Decoy_That_Shares_A_Wallet_With_The_Real_Profile()
    {
        var real = WalletProfile.OfOne(Wallet("real seed words"));
        real.Wallets.Add(Wallet("second real seed"));
        var decoy = WalletProfile.OfOne(Wallet("Second  REAL seed")); // same seed, different spacing/case

        using var mp = Pw(MainPw);
        using var dp = Pw(DuressPw);
        Assert.Throws<ArgumentException>(() => VaultManager.Create(_path, mp, real, dp, decoy, FastArgon));
        Assert.False(File.Exists(_path));
    }

    // ------------------------------------------------------------ the session

    [Fact]
    public void Session_Password_Change_Applies_To_Its_Own_Slot_Only()
    {
        CreateVault(withDecoy: true, decoyWipes: true);
        using (VaultSession s = Open(MainPw))
        {
            // The OTHER slot's password is refused like a wrong one — and its wipe must not fire.
            using (var dp = Pw(DuressPw))
            using (var np = Pw("coercer-chosen-pw"))
            {
                Assert.False(s.ChangePassword(dp, np));
            }

            using (var cur = Pw(MainPw))
            using (var np = Pw("new-main-password-789"))
            {
                Assert.True(s.ChangePassword(cur, np));
            }

            // The session goes on saving with the NEW key.
            s.Profile.ActiveWallet!.Name = "Renamed after the change";
            s.Save();
        }

        Assert.True((bool)Examine(DuressPw)["wipeOther"]!); // still pending: nothing fired
        using (var old = Pw(MainPw))
        {
            Assert.Null(VaultManager.Load(_path).Unlock(old));
        }

        using (var np = Pw("new-main-password-789"))
        {
            Assert.Equal("Renamed after the change", VaultManager.Load(_path).Unlock(np)!.Secrets.Name);
        }
    }

    [Fact]
    public void Session_Refuses_A_New_Password_That_Opens_The_Other_Slot()
    {
        CreateVault(withDecoy: true);
        using VaultSession s = Open(MainPw);
        using var cur = Pw(MainPw);
        using var dp = Pw(DuressPw);
        Assert.Throws<ArgumentException>(() => s.ChangePassword(cur, dp));
    }

    [Fact]
    public void Session_Checks_Its_Own_Password_Only()
    {
        CreateVault(withDecoy: true);
        using VaultSession s = Open(DuressPw);
        using var own = Pw(DuressPw);
        using var other = Pw(MainPw);
        using var wrong = Pw("nope-nope-nope");
        Assert.True(s.CheckPassword(own));
        Assert.False(s.CheckPassword(other));
        Assert.False(s.CheckPassword(wrong));
    }

    [Fact]
    public void Closed_Session_Can_Not_Save()
    {
        CreateVault(withDecoy: false);
        VaultSession s = Open(MainPw);
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(s.Save);
    }

    [Fact]
    public async Task Background_Save_Writes_The_Profile_As_It_Was_When_Called()
    {
        CreateVault(withDecoy: false);
        using (VaultSession s = Open(MainPw))
        {
            s.Profile.Contacts.Add(new Contact { Name = "Alice", Address = "4alice" });
            Task saving = s.SaveAsync();

            // The UI keeps changing the profile while the write runs: the write isn't affected.
            s.Profile.Contacts.Add(new Contact { Name = "Bob", Address = "4bob" });
            s.Profile.Wallets[0].Name = "Renamed meanwhile";
            await saving;
        }

        using VaultSession reopened = Open(MainPw);
        Assert.Equal(["Alice"], reopened.Profile.Contacts.Select(c => c.Name));
        Assert.Equal(WalletSecrets.DefaultName, reopened.Profile.Wallets[0].Name);
    }

    [Fact]
    public void Closed_Session_Can_Not_Save_In_The_Background()
    {
        CreateVault(withDecoy: false);
        VaultSession s = Open(MainPw);
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = s.SaveAsync(); }); // at once, not from the task
    }

    [Fact]
    public async Task Closing_Waits_For_A_Write_In_Flight()
    {
        CreateVault(withDecoy: false);
        VaultSession s = Open(MainPw);
        s.Profile.Contacts.Add(new Contact { Name = "Carol", Address = "4carol" });
        Task saving = s.SaveAsync();
        s.Dispose(); // lock: either the write already holds the session (and finishes), or it never starts
        try
        {
            await saving;
        }
        catch (ObjectDisposedException)
        {
            // closed before the write's turn: nothing was written
        }

        // Whichever happened, the vault opens and is whole.
        using VaultSession reopened = Open(MainPw);
        Assert.Single(reopened.Profile.Wallets);
        Assert.True(reopened.Profile.Contacts.Count is 0 or 1);
    }

    [Fact]
    public void Opening_A_Session_Never_Writes_A_Current_Vault()
    {
        CreateVault(withDecoy: true);
        byte[] before = File.ReadAllBytes(_path);
        using (Open(MainPw))
        using (Open(DuressPw))
        {
        }

        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    // ------------------------------------------------------------ vaults from 0.1–0.3

    [Fact]
    public void A_0_3_Vault_Opens_And_Becomes_A_Version_2_File()
    {
        File.WriteAllBytes(_path, LegacyFile((MainPw, V2Payload("real seed words")), (DuressPw, V2Payload("decoy seed words"))));

        using (var mp = Pw(MainPw))
        {
            UnlockResult r = VaultManager.Load(_path).Unlock(mp)!;
            Assert.Equal("real seed words", r.Secrets.Mnemonic);
            Assert.Equal(WalletSecrets.DefaultName, r.Secrets.Name);
            Assert.Equal(42UL, r.Secrets.RestoreHeight);
            Assert.Equal("ABCDEF", r.Secrets.EphemeralWalletPassword);
            Assert.False(r.UpgradedFromLegacyFormat); // 0.2/0.3 payloads need no notice
        }

        byte[] after = File.ReadAllBytes(_path);
        Assert.Equal(VaultFile.FileBytes, after.Length);
        Assert.Equal(2, after[4]);
        Assert.Equal(3, (int)Examine(MainPw)["v"]!);

        // The decoy was carried, not converted: its password still opens it, and converts it.
        using (var dp = Pw(DuressPw))
        {
            Assert.Equal("decoy seed words", VaultManager.Load(_path).Unlock(dp)!.Secrets.Mnemonic);
        }

        Assert.Equal(3, (int)Examine(DuressPw)["v"]!);
        using (var mp = Pw(MainPw))
        {
            Assert.Equal("real seed words", VaultManager.Load(_path).Unlock(mp)!.Secrets.Mnemonic);
        }
    }

    [Fact]
    public void A_Carried_Slot_Is_Kept_Byte_For_Byte_Until_Its_Own_Password_Opens_It()
    {
        byte[] legacy = LegacyFile((MainPw, V2Payload("real seed words")), (DuressPw, V2Payload("decoy seed words")));
        File.WriteAllBytes(_path, legacy);
        byte[] oldDecoySlot = legacy.AsSpan(HeaderBytes + LegacySlotBytes, LegacySlotBytes).ToArray();

        // Several saves through the real profile…
        for (int i = 0; i < 3; i++)
        {
            using VaultSession s = Open(MainPw);
            s.Profile.Contacts.Add(new Contact { Name = "c" + i, Address = "4…" });
            s.Save();
        }

        // …leave the old decoy slot exactly as it was, at the start of its new slot.
        byte[] now = File.ReadAllBytes(_path);
        Assert.True(oldDecoySlot.AsSpan().SequenceEqual(now.AsSpan(HeaderBytes + SlotBytes, LegacySlotBytes)));
        using var dp = Pw(DuressPw);
        Assert.Equal("decoy seed words", VaultManager.Load(_path).Unlock(dp)!.Secrets.Mnemonic);
    }

    [Fact]
    public void A_0_1_Vault_Is_Converted_And_Its_Marker_Removed_Slot_By_Slot()
    {
        File.WriteAllBytes(_path, LegacyFile((MainPw, V1Payload(0, "real seed words")), (DuressPw, V1Payload(1, "decoy seed words"))));

        using (var mp = Pw(MainPw))
        {
            Assert.True(VaultManager.Load(_path).Unlock(mp)!.UpgradedFromLegacyFormat);
        }

        JsonObject real = Examine(MainPw);
        Assert.False(real.ContainsKey("kind"));
        Assert.False(real.ContainsKey("label"));

        using (var dp = Pw(DuressPw))
        {
            UnlockResult r = VaultManager.Load(_path).Unlock(dp)!;
            Assert.True(r.UpgradedFromLegacyFormat); // reported for either slot alike
            Assert.Equal("decoy seed words", r.Secrets.Mnemonic);
        }

        Assert.False(Examine(DuressPw).ContainsKey("kind"));
    }

    [Fact]
    public void A_0_3_Decoy_With_Wipe_On_Duress_Still_Wipes_After_Conversion()
    {
        File.WriteAllBytes(_path, LegacyFile((MainPw, V2Payload("real seed words")), (DuressPw, V2Payload("decoy seed words", wipeOther: true))));

        // The real profile is opened (and converted) first, as most upgrades go…
        using (var mp = Pw(MainPw))
        {
            Assert.NotNull(VaultManager.Load(_path).Unlock(mp));
        }

        // …and the carried decoy keeps its wipe-on-duress.
        using (var dp = Pw(DuressPw))
        {
            Assert.Equal("decoy seed words", VaultManager.Load(_path).Unlock(dp)!.Secrets.Mnemonic);
        }

        using (var mp = Pw(MainPw))
        {
            Assert.Null(VaultManager.Load(_path).Unlock(mp));
        }

        Assert.False((bool)Examine(DuressPw)["wipeOther"]!);
    }

    [Fact]
    public void A_0_3_Single_Wallet_Vault_Converts_With_Its_Filler_Slot()
    {
        File.WriteAllBytes(_path, LegacyFile(null, (MainPw, V2Payload("real seed words"))));

        using (var mp = Pw(MainPw))
        using (VaultSession s = VaultManager.Load(_path).OpenSession(mp)!)
        {
            s.Profile.Wallets.Add(Wallet("another seed", "Spending"));
            s.Save();
        }

        Assert.Equal(VaultFile.FileBytes, File.ReadAllBytes(_path).Length);
        using var again = Pw(MainPw);
        Assert.Equal(2, VaultManager.Load(_path).Unlock(again)!.Profile.Wallets.Count);
        using var wrong = Pw(DuressPw);
        Assert.Null(VaultManager.Load(_path).Unlock(wrong));
    }

    [Fact]
    public void Old_Files_Of_The_Wrong_Size_Are_Refused()
    {
        byte[] legacy = LegacyFile((MainPw, V2Payload("x")), null);
        File.WriteAllBytes(_path, legacy[..^1]);
        Assert.Throws<InvalidDataException>(() => VaultManager.Load(_path));
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
