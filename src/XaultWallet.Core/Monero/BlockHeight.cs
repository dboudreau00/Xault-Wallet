using System.Globalization;

namespace XaultWallet.Core.Monero;

/// <summary>Culture-independent parsing of a typed block height.</summary>
public static class BlockHeight
{
    /// <summary>
    /// Digits with optional thousands separators. A height is an integer, so any of , . ' _ or a
    /// (no-break) space can only be grouping — unlike an amount, there is no decimal to confuse.
    /// </summary>
    public static bool TryParse(string? text, out ulong height)
    {
        string digits = new((text ?? string.Empty)
            .Where(c => c is not (',' or '.' or '\'' or '_' or ' ' or ' ' or ' '))
            .ToArray());
        height = 0;
        return digits.Length is > 0 and <= 12
               && digits.All(char.IsAsciiDigit)
               && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out height);
    }
}
