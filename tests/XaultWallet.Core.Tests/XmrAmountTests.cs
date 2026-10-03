using System.Globalization;
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
    public void Rejects_Non_Heights(string typed) => Assert.False(BlockHeight.TryParse(typed, out _));
}
