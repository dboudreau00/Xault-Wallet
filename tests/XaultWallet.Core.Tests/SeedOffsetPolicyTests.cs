using XaultWallet.Core.Models;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// These tests lock the single fund-safety invariant behind seed-offset support: a GENERATED seed
/// must never carry an offset (which would make the restore derive a different, empty wallet), while
/// an IMPORTED seed keeps exactly the offset the user supplied (trimmed).
/// </summary>
public class SeedOffsetPolicyTests
{
    [Theory]
    [InlineData("secret passphrase")]
    [InlineData("   spaced   ")]
    [InlineData("")]
    [InlineData(null)]
    public void Generated_Seed_Never_Carries_An_Offset(string? typed)
    {
        // The critical case: even if an offset value is lingering in the UI, a generated seed must
        // seal an EMPTY offset, or unlock would restore a different wallet than the one shown.
        Assert.Equal(string.Empty, SeedOffsetPolicy.ForSeed(wasGenerated: true, typed));
    }

    [Theory]
    [InlineData("passphrase", "passphrase")]
    [InlineData("  keep my spaces  ", "  keep my spaces  ")] // byte-for-byte: whitespace is significant
    [InlineData("CaseSensitive", "CaseSensitive")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Imported_Seed_Keeps_The_Offset_Exactly(string? typed, string expected)
    {
        // The offset is cn_slow_hash'd raw, so trimming or case-folding would open a DIFFERENT
        // wallet than the user created that passphrase with elsewhere. Preserve it byte-for-byte.
        Assert.Equal(expected, SeedOffsetPolicy.ForSeed(wasGenerated: false, typed));
    }
}
