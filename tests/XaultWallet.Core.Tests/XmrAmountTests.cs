using System.Globalization;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// The send amount must not depend on the OS locale. These are the exact inputs that a
/// culture-aware binding turned into 10–100× over-sends.
/// </summary>
public class XmrAmountTests
{
    public static IEnumerable<object[]> Cultures => new[] { "en-US", "de-DE", "fr-FR", "hi-IN", "ar-SA", "" }.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Parses_Identically_In_Every_Culture(string culture)
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(culture);

            Assert.Equal(0.25m, Parse("0,25")); // was 25 XMR on en-US
            Assert.Equal(1.5m, Parse("1,5"));   // was 15 XMR on en-US
            Assert.Equal(0.5m, Parse("0.5"));   // was 5 XMR on de-DE, 0 on fr-FR
            Assert.Equal(0.25m, Parse("0.25"));
            Assert.Equal(1234.5m, Parse("1234.5"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Theory]
    [InlineData("1,000", "1")]      // never read as one thousand: the parse can only err LOW
    [InlineData("1.000", "1")]
    [InlineData(".5", "0.5")]
    [InlineData("5.", "5")]
    [InlineData("  2.5  ", "2.5")]
    [InlineData("2.5 XMR", "2.5")]
    [InlineData("0.000000000001", "0.000000000001")] // one piconero
    [InlineData("18446744", "18446744")]
    public void Accepts(string typed, string expected) =>
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Parse(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData(".")]
    [InlineData("1,000.50")]           // two separators: refuse rather than guess
    [InlineData("1.000.000")]
    [InlineData("1 000")]              // grouping with spaces
    [InlineData("1'000")]
    [InlineData("1_000")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("0.0000000000001")]    // 13 decimals: below one piconero
    [InlineData("18446745")]           // above the representable maximum
    [InlineData("999999999")]
    public void Rejects(string typed)
    {
        Assert.False(XmrAmount.TryParse(typed, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("1,000", true)]
    [InlineData("12.500", true)]
    [InlineData("100,250", true)]
    [InlineData("0,125", false)]  // nobody groups a leading zero
    [InlineData("1,5", false)]
    [InlineData("1,0000", false)]
    [InlineData("1000", false)]
    [InlineData("1,000 XMR", true)]
    public void Flags_Input_That_Might_Have_Meant_Thousands(string typed, bool ambiguous) =>
        Assert.Equal(ambiguous, XmrAmount.LooksThousandsGrouped(typed));

    [Fact]
    public void Formats_With_A_Dot_Regardless_Of_Culture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1234.5", XmrAmount.Format(1234.5m));
            Assert.Equal("0.000000000001", XmrAmount.Format(1UL));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static decimal Parse(string s)
    {
        Assert.True(XmrAmount.TryParse(s, out decimal v, out string? error), error);
        return v;
    }
}

public class BlockHeightTests
{
    [Theory]
    [InlineData("3150000", 3150000UL)]
    [InlineData("3,150,000", 3150000UL)]
    [InlineData("3.150.000", 3150000UL)] // what a de-DE user types — silently ignored before
    [InlineData("3 150 000", 3150000UL)]
    [InlineData(" 42 ", 42UL)]
    [InlineData("0", 0UL)]
    [InlineData("1,000", 1000UL)]
    [InlineData("3'150'000", 3150000UL)]
    public void Accepts_Grouped_Or_Plain_Digits(string typed, ulong expected)
    {
        Assert.True(BlockHeight.TryParse(typed, out ulong h));
        Assert.Equal(expected, h);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-5")]
    [InlineData("12a")]
    [InlineData("1e6")]
    [InlineData("9999999999999")] // 13 digits: not a plausible height
    [InlineData("3150000.0")]     // was stripped to 31,500,000 — a height that hides every payment
    [InlineData("31,50,000")]     // not groups of three: refuse, don't guess
    [InlineData("3,150.000")]     // mixed separators
    [InlineData("3,150,00")]
    [InlineData(",150")]
    [InlineData("3,150,")]
    public void Rejects_Non_Heights(string typed) => Assert.False(BlockHeight.TryParse(typed, out _));
}

public class RestoreHeightTests
{
    private static readonly DateTimeOffset Jan2024 = DateTimeOffset.FromUnixTimeSeconds(1704067200); // 2024-01-01T00:00Z

    [Theory]
    // Hand-computed from wallet2::get_approximate_blockchain_height (monero master, 2026-10):
    // latest-fork block + (t - fork time) / 120 - per-network correction.
    [InlineData(MoneroNetwork.Mainnet, 3_051_325UL)]  // 2,689,608 + 395,317 - 33,600
    [InlineData(MoneroNetwork.Stagenet, 1_498_437UL)] // 1,151,720 + 395,317 - 48,600
    [InlineData(MoneroNetwork.Testnet, 2_384_035UL)]  // 1,983,520 + 427,115 - 26,600
    public void Estimate_Matches_Upstream_Formula(MoneroNetwork network, ulong expected) =>
        Assert.Equal(expected, RestoreHeights.ApproximateTip(network, Jan2024));

    [Fact]
    public void Estimate_Advances_One_Block_Per_Two_Minutes() =>
        Assert.Equal(720UL, RestoreHeights.ApproximateTip(MoneroNetwork.Mainnet, Jan2024.AddDays(1)) - RestoreHeights.ApproximateTip(MoneroNetwork.Mainnet, Jan2024));

    [Fact]
    public void A_Clock_Before_Genesis_Gives_Zero_Not_An_Underflow() =>
        Assert.Equal(0UL, RestoreHeights.ApproximateTip(MoneroNetwork.Mainnet, DateTimeOffset.UnixEpoch));

    [Fact]
    public void A_Node_Below_The_Estimate_Is_Used_As_Reported()
    {
        ulong tip = RestoreHeights.ApproximateTip(MoneroNetwork.Mainnet, Jan2024) - 5_000;
        Assert.Equal(tip - RestoreHeights.SafetyMargin, RestoreHeights.ForNewSeed(tip, MoneroNetwork.Mainnet, Jan2024));
    }

    [Fact]
    public void A_Node_Claiming_A_Far_Future_Height_Is_Capped_By_The_Clock()
    {
        ulong estimate = RestoreHeights.ApproximateTip(MoneroNetwork.Mainnet, Jan2024);
        Assert.Equal(estimate - RestoreHeights.SafetyMargin, RestoreHeights.ForNewSeed(99_999_999, MoneroNetwork.Mainnet, Jan2024));
    }

    [Fact]
    public void A_Young_Private_Chain_Uses_Its_Real_Tip()
    {
        Assert.Equal(1_000UL - RestoreHeights.SafetyMargin, RestoreHeights.ForNewSeed(1_000, MoneroNetwork.Mainnet, Jan2024));
        Assert.Equal(0UL, RestoreHeights.ForNewSeed(300, MoneroNetwork.Mainnet, Jan2024));
    }
}
