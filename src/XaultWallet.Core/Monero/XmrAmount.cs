using System.Globalization;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Parsing and formatting of user-facing XMR amounts, independent of the OS culture.
///
/// Why not a culture-aware numeric binding: it treats the culture's GROUP separator as valid input.
/// Verified with Avalonia 11.1.3 bindings: on en-US "0,25" became 25 XMR (100×) and "1,5" became 15;
/// on de-DE "0.5" became 5. An irreversible send amount cannot depend on the user's locale.
///
/// Rule: '.' or ',' is ALWAYS the decimal point and there are no thousands separators. Any input a
/// user could have meant as thousands-grouped ("1,000") therefore parses SMALLER than intended,
/// never larger — an under-send is recoverable, an over-send is not — and the UI shows the parsed
/// value before anything is built.
/// </summary>
public static class XmrAmount
{
    /// <summary>Monero's smallest unit is 1e-12 XMR (one piconero).</summary>
    public const int MaxDecimals = 12;

    public static bool TryParse(string? text, out decimal xmr, out string? error)
    {
        xmr = 0m;
        error = null;
        string s = (text ?? string.Empty).Trim();
        if (s.EndsWith("xmr", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^3].TrimEnd(); // tolerate a pasted "1.5 XMR"
        }

        if (s.Length == 0)
        {
            error = "Enter an amount.";
            return false;
        }

        int separators = 0, separatorAt = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is >= '0' and <= '9')
            {
                continue;
            }

            if (c is '.' or ',')
            {
                separators++;
                separatorAt = i;
                continue;
            }

            error = c is ' ' or ' ' or ' ' or '\'' or '_'
                ? "Don't use thousands separators — type the amount like 1234.5."
                : $"'{c}' isn't allowed in an amount.";
            return false;
        }

        if (separators > 1)
        {
            error = "Use one decimal point and no thousands separators — e.g. 1234.5.";
            return false;
        }

        string whole = separatorAt < 0 ? s : s[..separatorAt];
        string fraction = separatorAt < 0 ? string.Empty : s[(separatorAt + 1)..];
        if (whole.Length == 0 && fraction.Length == 0)
        {
            error = "Enter an amount.";
            return false;
        }

        if (fraction.Length > MaxDecimals)
        {
            error = "Monero amounts have at most 12 decimal places.";
            return false;
        }

        if (whole.TrimStart('0').Length > 8)
        {
            error = "That amount is larger than the total Monero supply.";
            return false;
        }

        decimal value = decimal.Parse(
            (whole.Length == 0 ? "0" : whole) + (fraction.Length == 0 ? string.Empty : "." + fraction),
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture);

        if (value <= 0m)
        {
            error = "Enter an amount greater than zero.";
            return false;
        }

        if (value > MoneroRpcClient.MaxXmrAmount)
        {
            error = "That amount is larger than the total Monero supply.";
            return false;
        }

        xmr = value;
        return true;
    }

    /// <summary>
    /// True for input a user could have meant as thousands-grouped: 1–3 leading digits (not "0"),
    /// one separator, exactly three digits after it ("1,000", "12.500"). It still parses as a
    /// decimal; the UI uses this to point out how it was read.
    /// </summary>
    public static bool LooksThousandsGrouped(string? text)
    {
        string s = (text ?? string.Empty).Trim();
        if (s.EndsWith("xmr", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^3].TrimEnd(); // same tolerance as TryParse: "1,000 XMR"
        }

        int sep = s.IndexOfAny(['.', ',']);
        return sep is >= 1 and <= 3
               && s[0] != '0'
               && s.Length - sep - 1 == 3
               && s.LastIndexOfAny(['.', ',']) == sep
               && s.Where((c, i) => i != sep).All(char.IsAsciiDigit);
    }

    /// <summary>Canonical display: '.' decimal point, up to 12 decimals, no grouping, any culture.</summary>
    public static string Format(decimal xmr) => xmr.ToString("0.############", CultureInfo.InvariantCulture);

    public static string Format(ulong atomic) => Format(MoneroRpcClient.AtomicToXmr(atomic));
}
