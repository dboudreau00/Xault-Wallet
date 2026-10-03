using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// Tests for the fund-protecting guardrails: password/seed collision rejection at vault
/// creation, collision rejection on password change, duress-wipe resilience, amount
/// conversion bounds, and address sanity checks.
/// </summary>
public class VaultSafeguardTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"xvtest_{Guid.NewGuid():N}.xv");
    private static readonly VaultCrypto.Argon2Parameters FastArgon = new(19_456, 2, 1);

    private static SecureBuffer Pw(string s) => SecureBuffer.FromPassword(s.ToCharArray());

    private static WalletSecrets Secrets(string mnemonic, bool wipe = false) =>
        new() { Mnemonic = mnemonic, WipeOtherSlotOnUnlock = wipe };

    [Fact]
    public void Create_Rejects_Duress_Password_Equal_To_Main()
    {
        // One password matching both slots makes every unlock a coin flip — and with
        // wipe-on-duress, a "normal" unlock could destroy the real wallet.
        using var mp = Pw("same-password-123");
        using var dp = Pw("same-password-123");
        Assert.Throws<ArgumentException>(() =>
            VaultManager.Create(_path, mp, Secrets("real seed"), dp, Secrets("decoy seed"), FastArgon));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Create_Rejects_Same_Mnemonic_In_Both_Slots()
    {
        using var mp = Pw("main-password-123");
        using var dp = Pw("duress-password-456");
        Assert.Throws<ArgumentException>(() =>
            VaultManager.Create(_path, mp, Secrets("identical seed words"), dp, Secrets("  IDENTICAL   seed words "), FastArgon));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Create_Rejects_Empty_Main_Password()
    {
        using var mp = Pw("");
        Assert.Throws<ArgumentException>(() =>
            VaultManager.Create(_path, mp, Secrets("real seed"), argon: FastArgon));
    }

    [Fact]
    public void Create_Rejects_Half_Specified_Duress_Profile()
    {
        // Password without secrets (and vice versa) would silently create a vault with NO
        // decoy while the user believes one exists.
        using var mp = Pw("main-password-123");
        using var dp = Pw("duress-password-456");
        Assert.Throws<ArgumentException>(() =>
            VaultManager.Create(_path, mp, Secrets("real seed"), dp, null, FastArgon));
    }

    [Fact]
    public void ChangePassword_Rejects_Password_That_Opens_The_Other_Slot()
    {
        using (var mp = Pw("main-password-123"))
        using (var dp = Pw("duress-password-456"))
        {
            VaultManager.Create(_path, mp, Secrets("real seed"), dp, Secrets("decoy seed"), FastArgon);
        }

        var mgr = VaultManager.Load(_path);
        using var cur = Pw("main-password-123");
        using var collides = Pw("duress-password-456");
        Assert.Throws<ArgumentException>(() => mgr.ChangePassword(cur, collides));

        // The vault must be unchanged: both original passwords still work.
        var mgr2 = VaultManager.Load(_path);
        using var mp2 = Pw("main-password-123");
        Assert.NotNull(mgr2.Unlock(mp2));
        var mgr3 = VaultManager.Load(_path);
        using var dp2 = Pw("duress-password-456");
        UnlockResult? duress = mgr3.Unlock(dp2);
        Assert.NotNull(duress);
        Assert.Equal("decoy seed", duress!.Secrets.Mnemonic);
    }

    [Fact]
    public void ChangePassword_Still_Works_For_A_Distinct_New_Password()
    {
        using (var mp = Pw("main-password-123"))
        using (var dp = Pw("duress-password-456"))
        {
            VaultManager.Create(_path, mp, Secrets("real seed"), dp, Secrets("decoy seed"), FastArgon);
        }

        var mgr = VaultManager.Load(_path);
        using (var cur = Pw("main-password-123"))
        using (var next = Pw("brand-new-password-789"))
        {
            Assert.True(mgr.ChangePassword(cur, next));
        }

        var mgr2 = VaultManager.Load(_path);
        using var np = Pw("brand-new-password-789");
        UnlockResult? r = mgr2.Unlock(np);
        Assert.NotNull(r);
        Assert.Equal("real seed", r!.Secrets.Mnemonic);
    }

    [Fact]
    public void Duress_Wipe_Survives_A_Persist_Failure_And_Still_Opens_The_Decoy()
    {
        using (var mp = Pw("main-password-123"))
        using (var dp = Pw("duress-password-456"))
        {
            VaultManager.Create(_path, mp, Secrets("real seed"), dp, Secrets("decoy seed", wipe: true), FastArgon);
        }

        // Only Windows enforces FileShare.None as a mandatory lock; on Linux/macOS it is
        // advisory and rename() succeeds anyway, so the "persist fails" premise never holds
        // and the test would be vacuously green. Skip rather than pretend.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mgr = VaultManager.Load(_path);

        // Simulate the disk write failing at the worst moment (AV lock, read-only media):
        // hold the vault file open with no sharing so File.Replace/Move cannot touch it.
        using (var fileLock = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var dp2 = Pw("duress-password-456");
            UnlockResult? r = mgr.Unlock(dp2);

            // Under coercion the decoy MUST still open normally — an error here would make a
            // duress unlock visibly different from a normal one.
            Assert.NotNull(r);
            Assert.Equal("decoy seed", r!.Secrets.Mnemonic);
        }
    }

    [Fact]
    public void Duress_Wipe_Destroys_The_Real_Slot_When_The_Write_Succeeds()
    {
        using (var mp = Pw("main-password-123"))
        using (var dp = Pw("duress-password-456"))
        {
            VaultManager.Create(_path, mp, Secrets("real seed"), dp, Secrets("decoy seed", wipe: true), FastArgon);
        }

        var mgr = VaultManager.Load(_path);
        using (var dp2 = Pw("duress-password-456"))
        {
            UnlockResult? r = mgr.Unlock(dp2);
            Assert.NotNull(r);
            Assert.Equal("decoy seed", r!.Secrets.Mnemonic);
        }

        // After the wipe the real password opens nothing; the duress password still works.
        var after = VaultManager.Load(_path);
        using var mp2 = Pw("main-password-123");
        Assert.Null(after.Unlock(mp2));
        var after2 = VaultManager.Load(_path);
        using var dp3 = Pw("duress-password-456");
        Assert.NotNull(after2.Unlock(dp3));
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // temp cleanup only
        }
    }
}

public class AmountConversionTests
{
    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(1, 1_000_000_000_000UL)]
    [InlineData(0.5, 500_000_000_000UL)]
    [InlineData(0.000000000001, 1UL)] // one piconero
    public void XmrToAtomic_Converts_Exactly(decimal xmr, ulong expected) =>
        Assert.Equal(expected, MoneroRpcClient.XmrToAtomic(xmr));

    [Fact]
    public void XmrToAtomic_Rejects_Negative_Amounts() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MoneroRpcClient.XmrToAtomic(-0.1m));

    [Fact]
    public void XmrToAtomic_Rejects_Amounts_Above_Total_Supply()
    {
        // Without the bound this is an uncaught OverflowException — a crash from a typo
        // in the amount field.
        Assert.Throws<ArgumentOutOfRangeException>(() => MoneroRpcClient.XmrToAtomic(19_000_000m));
    }

    [Fact]
    public void XmrToAtomic_Handles_Its_Own_Maximum_Without_Overflow()
    {
        // The cap must sit at or below ulong capacity in atomic units, or the "guarded"
        // top range would still hit the raw OverflowException the guard exists to prevent.
        ulong atomic = MoneroRpcClient.XmrToAtomic(MoneroRpcClient.MaxXmrAmount);
        Assert.True(atomic > 0);
    }

    [Fact]
    public void XmrToAtomic_Rejects_The_Former_Overflow_Gap()
    {
        // 18,460,000 XMR is above ulong capacity in atomic units (~18,446,744 XMR): it must be
        // an ArgumentOutOfRangeException, never a raw OverflowException.
        Assert.Throws<ArgumentOutOfRangeException>(() => MoneroRpcClient.XmrToAtomic(18_460_000m));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(1_000_000_000_000UL)]
    [InlineData(123_456_789_012_345UL)]
    public void Atomic_To_Xmr_Round_Trips(ulong atomic) =>
        Assert.Equal(atomic, MoneroRpcClient.XmrToAtomic(MoneroRpcClient.AtomicToXmr(atomic)));
}

public class MoneroAddressTests
{
    // Structurally valid base58 strings of the right length (not real wallets).
    private static string Addr(char prefix, int length = 95) => prefix + new string('A', length - 1);

    [Fact]
    public void Accepts_A_Wellformed_Mainnet_Address() =>
        Assert.Null(MoneroAddress.Problem(Addr('4'), MoneroNetwork.Mainnet));

    [Fact]
    public void Accepts_A_Wellformed_Integrated_Mainnet_Address() =>
        Assert.Null(MoneroAddress.Problem(Addr('4', 106), MoneroNetwork.Mainnet));

    [Fact]
    public void Rejects_Empty_Input() =>
        Assert.NotNull(MoneroAddress.Problem("", MoneroNetwork.Mainnet));

    [Fact]
    public void Rejects_A_Truncated_Address() =>
        Assert.NotNull(MoneroAddress.Problem(Addr('4', 60), MoneroNetwork.Mainnet));

    [Fact]
    public void Rejects_Invalid_Base58_Characters()
    {
        // '0', 'O', 'I', 'l' are not in the base58 alphabet.
        string addr = '4' + new string('A', 93) + '0';
        Assert.NotNull(MoneroAddress.Problem(addr, MoneroNetwork.Mainnet));
    }

    [Theory]
    [InlineData('9')] // testnet standard
    [InlineData('5')] // stagenet standard
    public void Rejects_Wrong_Network_Prefix_On_Mainnet(char prefix) =>
        Assert.NotNull(MoneroAddress.Problem(Addr(prefix), MoneroNetwork.Mainnet));

    [Fact]
    public void Rejects_Mainnet_Address_On_Stagenet() =>
        Assert.NotNull(MoneroAddress.Problem(Addr('4'), MoneroNetwork.Stagenet));

    [Fact]
    public void Tolerates_Surrounding_Whitespace() =>
        Assert.Null(MoneroAddress.Problem("  " + Addr('4') + "  ", MoneroNetwork.Mainnet));
}
