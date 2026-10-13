using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>Text from outside the app, as it may be shown: nothing in it changes how the text around it reads.</summary>
public class UntrustedTextTests
{
    /// <summary>One character by code point (a lone surrogate too), so no invisible character sits in this file.</summary>
    internal static string Ch(int codePoint) =>
        codePoint is >= 0xD800 and <= 0xDFFF ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint);

    [Fact]
    public void Plain_Text_Is_Kept()
    {
        string plain = "Rent & utilities, July (Caf" + Ch(0xE9) + ", " + Ch(0x6771) + Ch(0x4EAC) + ", " + Ch(0x1F600) + ")";
        Assert.False(UntrustedText.HasHiddenCharacters(plain));
        Assert.Equal(plain, UntrustedText.Clean(plain, 100));
        Assert.Equal("Rent & utilities, July", UntrustedText.Clean("  Rent &   utilities,\tJuly \r\n", 100));
        Assert.Equal(string.Empty, UntrustedText.Clean(null, 10));
    }

    [Theory]
    [InlineData(0x0007)]  // a control character
    [InlineData(0x0085)]  // next line
    [InlineData(0x202E)]  // right-to-left override
    [InlineData(0x2067)]  // right-to-left isolate
    [InlineData(0x200B)]  // zero-width space
    [InlineData(0xFEFF)]  // byte order mark
    [InlineData(0x2028)]  // line separator
    [InlineData(0x2029)]  // paragraph separator
    [InlineData(0xE000)]  // private use
    [InlineData(0xE0041)] // a tag character, outside the BMP
    [InlineData(0xD800)]  // a lone high surrogate
    [InlineData(0xDC00)]  // a lone low surrogate
    public void Hidden_Characters_Are_Found_And_Dropped(int codePoint)
    {
        string hidden = Ch(codePoint);
        Assert.True(UntrustedText.HasHiddenCharacters("a" + hidden + "b"));
        Assert.DoesNotContain(hidden, UntrustedText.Clean("a" + hidden + "b", 100), StringComparison.Ordinal);
    }

    [Fact]
    public void Separators_Become_Spaces_And_Overrides_Vanish()
    {
        Assert.Equal("Pay Alice", UntrustedText.Clean("Pay" + Ch(0x2028) + "Alice", 100));
        Assert.Equal("Pay ecilA", UntrustedText.Clean("Pay " + Ch(0x202E) + "ecilA", 100));
    }

    [Fact]
    public void Shortening_Never_Splits_A_Surrogate_Pair()
    {
        string smile = Ch(0x1F600);
        Assert.Equal(smile + smile + "...", UntrustedText.Clean(string.Concat(Enumerable.Repeat(smile, 10)), 8));
        Assert.Equal("abcd...", UntrustedText.Clean("abcdefghij", 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => UntrustedText.Clean("x", 3));
    }
}
