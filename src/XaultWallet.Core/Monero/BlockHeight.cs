using System.Globalization;

namespace XaultWallet.Core.Monero;

/// <summary>Culture-independent parsing of a typed block height.</summary>
public static class BlockHeight
{
    private static readonly char[] Separators = [',', '.', '\'', '_', ' ', ' ', ' '];

    /// <summary>
    /// Plain digits, or digits grouped in threes with ONE kind of separator ("3,150,000",
    /// "3.150.000", "3 150 000"). Anything else is refused rather than guessed: a height read too
    /// HIGH (e.g. "3150000.0" stripped to 31,500,000) would hide every payment below it.
    /// </summary>
    public static bool TryParse(string? text, out ulong height)
    {
        height = 0;
        string s = (text ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return false;
        }

        int firstSep = s.IndexOfAny(Separators);
        if (firstSep >= 0)
        {
            char sep = s[firstSep];
            string[] groups = s.Split(sep);
            bool grouped = groups[0].Length is >= 1 and <= 3
                           && groups.Skip(1).All(g => g.Length == 3)
                           && groups.All(g => g.All(char.IsAsciiDigit));
            if (!grouped)
            {
                return false;
            }

            s = string.Concat(groups);
        }

        return s.Length <= 12
               && s.All(char.IsAsciiDigit)
               && ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out height);
    }
}
