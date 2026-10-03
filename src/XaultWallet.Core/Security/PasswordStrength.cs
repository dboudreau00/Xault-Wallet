namespace XaultWallet.Core.Security;

public enum StrengthLevel { Empty, VeryWeak, Weak, Fair, Strong, VeryStrong }

/// <summary>
/// A conservative entropy estimator. It is NOT zxcvbn — no full dictionary model — but beyond the
/// character-pool estimate it discounts the patterns people actually use: repeated characters
/// ("aaaa"), sequences ("1234", "abcd", "dcba"), keyboard runs ("qwerty", "asdf") and a short list of
/// the most common passwords. The vault file can be attacked offline, so the app refuses
/// <see cref="StrengthLevel.VeryWeak"/> outright and shows the estimate for everything else.
/// </summary>
public static class PasswordStrength
{
    /// <summary>The lowest level the app accepts for a vault password.</summary>
    public const StrengthLevel MinimumAccepted = StrengthLevel.Weak;

    private static readonly string[] KeyboardRows = ["1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm"];

    // A few of the most common passwords and their trivial variants. Matched case-insensitively on
    // the password with trailing digits/symbols stripped ("Password123!" -> "password").
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "password", "passw0rd", "qwerty", "qwertyuiop", "letmein", "welcome", "admin", "iloveyou",
        "monkey", "dragon", "football", "baseball", "sunshine", "princess", "master", "shadow",
        "monero", "bitcoin", "crypto", "wallet", "secret", "changeme", "abc", "abcdef", "abcdefgh",
        "trustno1", "superman", "batman", "starwars", "whatever", "freedom", "hello", "login",
    };

    public static (StrengthLevel level, double bitsEstimate) Evaluate(ReadOnlySpan<char> pw)
    {
        if (pw.Length == 0)
        {
            return (StrengthLevel.Empty, 0);
        }

        bool lower = false, upper = false, digit = false, symbol = false;
        foreach (char c in pw)
        {
            if (char.IsLower(c)) { lower = true; }
            else if (char.IsUpper(c)) { upper = true; }
            else if (char.IsDigit(c)) { digit = true; }
            else { symbol = true; }
        }

        int pool = (lower ? 26 : 0) + (upper ? 26 : 0) + (digit ? 10 : 0) + (symbol ? 33 : 0);
        pool = Math.Max(pool, 2);

        // Characters that merely continue a pattern add almost nothing for an attacker.
        int predictable = CountPredictable(pw);
        double effectiveLength = pw.Length - (0.8 * predictable);
        double bits = Math.Max(0, effectiveLength) * Math.Log2(pool);

        // Penalise low variety and short length.
        int classes = (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0);
        if (classes <= 1) { bits *= 0.6; }
        if (pw.Length < 8) { bits *= 0.5; }

        if (IsCommon(pw))
        {
            bits = Math.Min(bits, 10);
        }

        StrengthLevel level = bits switch
        {
            < 28 => StrengthLevel.VeryWeak,
            < 40 => StrengthLevel.Weak,
            < 60 => StrengthLevel.Fair,
            < 80 => StrengthLevel.Strong,
            _ => StrengthLevel.VeryStrong,
        };

        return (level, bits);
    }

    /// <summary>
    /// Count characters (from the third of a run on) that continue a repeat, an ascending or
    /// descending sequence, or a keyboard-row run. The first two characters of any run count as
    /// free: "ab" is two choices, "abcdef" is mostly one.
    /// </summary>
    private static int CountPredictable(ReadOnlySpan<char> pw)
    {
        int predictable = 0;
        for (int i = 2; i < pw.Length; i++)
        {
            char a = char.ToLowerInvariant(pw[i - 2]), b = char.ToLowerInvariant(pw[i - 1]), c = char.ToLowerInvariant(pw[i]);
            bool repeat = a == b && b == c;
            bool sequence = (b - a == 1 && c - b == 1) || (a - b == 1 && b - c == 1);
            if (repeat || sequence || KeyboardStep(a, b, c))
            {
                predictable++;
            }
        }

        return predictable;
    }

    private static bool KeyboardStep(char a, char b, char c)
    {
        foreach (string row in KeyboardRows)
        {
            int ia = row.IndexOf(a), ib = row.IndexOf(b), ic = row.IndexOf(c);
            if (ia >= 0 && ib >= 0 && ic >= 0 && ((ib - ia == 1 && ic - ib == 1) || (ia - ib == 1 && ib - ic == 1)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCommon(ReadOnlySpan<char> pw)
    {
        string core = new string(pw).ToLowerInvariant().TrimEnd("0123456789!@#$%^&*.?_-+ ".ToCharArray());
        return core.Length == 0 || Common.Contains(core);
    }
}
