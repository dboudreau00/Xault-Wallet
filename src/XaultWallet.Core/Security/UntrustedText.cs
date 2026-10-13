using System.Buffers;
using System.Globalization;
using System.Text;

namespace XaultWallet.Core.Security;

/// <summary>
/// Text from outside the app (a payment link's note, a label or a path from the browser's side) as it
/// may be shown: nothing in it may change how the text around it reads. Hidden characters are control
/// and format characters (line breaks, the bidirectional overrides that make text read backwards,
/// invisible joiners, tag characters), line and paragraph separators, private-use and unassigned code
/// points, and broken UTF-16 (a lone surrogate).
/// </summary>
public static class UntrustedText
{
    /// <summary>True when <paramref name="text"/> has a hidden character (see the class summary).</summary>
    public static bool HasHiddenCharacters(string? text)
    {
        ReadOnlySpan<char> rest = text;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out Rune rune, out int used) != OperationStatus.Done || IsHidden(rune))
            {
                return true;
            }

            rest = rest[used..];
        }

        return false;
    }

    /// <summary>
    /// <paramref name="text"/> made safe to show: hidden characters dropped, every run of whitespace (tabs
    /// and line breaks too) one space, trimmed. Longer than <paramref name="maxLength"/> UTF-16 units, it
    /// is cut between characters (never inside a surrogate pair) and ends in "...".
    /// </summary>
    public static string Clean(string? text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 4);
        var sb = new StringBuilder();
        bool space = false;
        ReadOnlySpan<char> rest = text;
        while (!rest.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(rest, out Rune rune, out int used);
            rest = rest[used..];
            if (status != OperationStatus.Done)
            {
                continue; // a lone surrogate
            }

            if (Rune.IsWhiteSpace(rune))
            {
                space = sb.Length > 0;
                continue;
            }

            if (IsHidden(rune))
            {
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(rune.ToString());
        }

        if (sb.Length <= maxLength)
        {
            return sb.ToString();
        }

        int cut = maxLength - 3;
        if (char.IsLowSurrogate(sb[cut]))
        {
            cut--; // keep the pair whole
        }

        return sb.ToString(0, cut).TrimEnd() + "...";
    }

    private static bool IsHidden(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate;
}
