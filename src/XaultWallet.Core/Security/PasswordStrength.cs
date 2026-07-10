namespace XaultWallet.Core.Security;

public enum StrengthLevel { Empty, VeryWeak, Weak, Fair, Strong, VeryStrong }

/// <summary>
/// A lightweight entropy estimator. This is NOT a substitute for zxcvbn — it does
/// not model dictionary words or keyboard walks — but it gives the user honest,
/// conservative feedback and blocks obviously terrible passwords. For a
/// production build, wire in the zxcvbn-cs package instead.
/// </summary>
public static class PasswordStrength
{
    public static (StrengthLevel level, double bitsEstimate) Evaluate(ReadOnlySpan<char> pw)
    {
        if (pw.Length == 0)
        {
            return (StrengthLevel.Empty, 0);
        }

        int pool = 0;
        bool lower = false, upper = false, digit = false, symbol = false;
        foreach (char c in pw)
        {
            if (char.IsLower(c)) { lower = true; }
            else if (char.IsUpper(c)) { upper = true; }
            else if (char.IsDigit(c)) { digit = true; }
            else { symbol = true; }
        }

        if (lower) { pool += 26; }
        if (upper) { pool += 26; }
        if (digit) { pool += 10; }
        if (symbol) { pool += 33; }
        if (pool == 0) { pool = 1; }

        double bits = pw.Length * Math.Log2(pool);

        // Penalise low variety and short length.
        int classes = (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0);
        if (classes <= 1) { bits *= 0.6; }
        if (pw.Length < 8) { bits *= 0.5; }

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
}
