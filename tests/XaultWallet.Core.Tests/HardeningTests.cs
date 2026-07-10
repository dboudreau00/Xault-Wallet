using System.Text;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

public class VaultFileTests
{
    private static readonly VaultCrypto.Argon2Parameters FastArgon = new(19_456, 2, 1);

    private static SecureBuffer Pw(string s) => SecureBuffer.FromPassword(s.ToCharArray());

    [Fact]
    public void Slots_RoundTrip_Through_Serialize()
    {
        var file = VaultFile.CreateEmpty(FastArgon);
        using (var a = Pw("alpha-pass"))
        using (var b = Pw("bravo-pass"))
        {
            file.WriteSlot(0, a, Encoding.UTF8.GetBytes("real-payload"));
            file.WriteSlot(1, b, Encoding.UTF8.GetBytes("decoy-payload"));
        }

        byte[] bytes = file.Serialize();
        VaultFile reloaded = VaultFile.Deserialize(bytes);

        using (var a = Pw("alpha-pass"))
        {
            var hit = reloaded.TryUnlock(a);
            Assert.NotNull(hit);
            Assert.Equal("real-payload", Encoding.UTF8.GetString(hit!.Value.plaintext.Span));
            hit.Value.plaintext.Dispose();
        }

        using (var wrong = Pw("nope"))
        {
            Assert.Null(reloaded.TryUnlock(wrong));
        }
    }

    [Fact]
    public void All_Slots_Are_Identical_Size_Regardless_Of_Payload()
    {
        var file = VaultFile.CreateEmpty(FastArgon);
        using (var a = Pw("x"))
        {
            file.WriteSlot(0, a, Encoding.UTF8.GetBytes("tiny"));
        }
        // Slot 1 remains random filler. Serialized size must be fixed either way.
        int withOneSlot = file.Serialize().Length;

        using (var b = Pw("y"))
        {
            file.WriteSlot(1, b, Encoding.UTF8.GetBytes(new string('z', 2000)));
        }
        int withTwoSlots = file.Serialize().Length;

        Assert.Equal(withOneSlot, withTwoSlots);
    }

    [Fact]
    public void Oversized_Payload_Is_Rejected()
    {
        var file = VaultFile.CreateEmpty(FastArgon);
        using var a = Pw("x");
        byte[] tooBig = new byte[VaultFile.PaddedPlaintextBytes]; // no room for the 4-byte length prefix
        Assert.Throws<ArgumentException>(() => file.WriteSlot(0, a, tooBig));
    }

    [Fact]
    public void Bad_Magic_Is_Rejected()
    {
        byte[] bytes = VaultFile.CreateEmpty(FastArgon).Serialize();
        bytes[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => VaultFile.Deserialize(bytes));
    }

    [Fact]
    public void Truncated_File_Is_Rejected()
    {
        byte[] bytes = VaultFile.CreateEmpty(FastArgon).Serialize();
        byte[] shorter = bytes[..^1];
        Assert.Throws<InvalidDataException>(() => VaultFile.Deserialize(shorter));
    }

    [Fact]
    public void Tampered_Kdf_Memory_Is_Rejected()
    {
        byte[] bytes = VaultFile.CreateEmpty(FastArgon).Serialize();
        // argonMem lives at offset 8 (after magic4+ver1+slots1+reserved2). Force an absurd value.
        bytes[8] = 0xFF; bytes[9] = 0xFF; bytes[10] = 0xFF; bytes[11] = 0xFF;
        Assert.Throws<InvalidDataException>(() => VaultFile.Deserialize(bytes));
    }
}

public class Argon2ParameterTests
{
    [Theory]
    [InlineData(1024, 4, 4)]        // memory too low
    [InlineData(5_000_000, 4, 4)]   // memory too high
    [InlineData(65_536, 1, 4)]      // iterations too low
    [InlineData(65_536, 4, 0)]      // parallelism too low
    public void Invalid_Parameters_Throw(int mem, int it, int par)
    {
        var p = new VaultCrypto.Argon2Parameters(mem, it, par);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Validate());
    }

    [Fact]
    public void Default_Parameters_Are_Valid()
    {
        VaultCrypto.Argon2Parameters.Default.Validate(); // must not throw
    }
}

public class SecureBufferTests
{
    [Fact]
    public void FromPassword_Clears_The_Source_Chars()
    {
        char[] chars = "hunter2".ToCharArray();
        using SecureBuffer buf = SecureBuffer.FromPassword(chars);

        Assert.All(chars, c => Assert.Equal('\0', c));
        Assert.Equal(Encoding.UTF8.GetBytes("hunter2"), buf.ToArray());
    }

    [Fact]
    public void Disposed_Buffer_Throws_On_Access()
    {
        var buf = new SecureBuffer(16);
        buf.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buf.Span.ToArray());
    }
}

public class PasswordStrengthTests
{
    [Fact]
    public void Empty_Is_Empty() =>
        Assert.Equal(StrengthLevel.Empty, PasswordStrength.Evaluate("").level);

    [Fact]
    public void Short_Simple_Is_Weak()
    {
        StrengthLevel level = PasswordStrength.Evaluate("abcd").level;
        Assert.True(level is StrengthLevel.VeryWeak or StrengthLevel.Weak);
    }

    [Fact]
    public void Long_Complex_Is_Strong()
    {
        StrengthLevel level = PasswordStrength.Evaluate("Tr0ub4dour&3xploreLongPhrase!").level;
        Assert.True(level is StrengthLevel.Strong or StrengthLevel.VeryStrong);
    }
}
