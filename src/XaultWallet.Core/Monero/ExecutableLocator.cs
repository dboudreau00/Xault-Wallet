namespace XaultWallet.Core.Monero;

/// <summary>
/// Resolves a bare executable name the way a shell would, to an ABSOLUTE path. The launcher only
/// ever starts an absolute path it has checked exists — handing a bare name to Process.Start would
/// let the OS search the current directory and other places the user never chose.
/// </summary>
public static class ExecutableLocator
{
    /// <summary>The platform's wallet-rpc file name.</summary>
    public static string WalletRpcFileName => OperatingSystem.IsWindows() ? "monero-wallet-rpc.exe" : "monero-wallet-rpc";

    /// <summary>
    /// First existing <paramref name="fileName"/> in the directories of <paramref name="pathVariable"/>
    /// (a PATH-style list), or null. Relative and empty entries are skipped: a relative PATH entry
    /// resolves against the current directory, which is exactly the lookup this exists to avoid.
    /// </summary>
    public static string? FindOnPath(string fileName, string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (string raw in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = raw.Trim().Trim('"');
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir))
            {
                continue;
            }

            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
