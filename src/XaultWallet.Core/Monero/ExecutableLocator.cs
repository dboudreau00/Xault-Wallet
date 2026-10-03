namespace XaultWallet.Core.Monero;

/// <summary>
/// Resolves a bare executable name the way a shell would, to an ABSOLUTE path, and guards the
/// launch: wallet-rpc is only ever started from a fully-qualified path that exists — handing a bare
/// or relative name to Process.Start would let the OS search the current directory and other
/// places the user never chose.
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

            // Path.Combine returns fileName itself when it is rooted ("C:x.exe" on Windows), so the
            // candidate is re-checked: only a fully-qualified hit is ever returned.
            string candidate = Path.Combine(dir, fileName);
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// A user-entered binary location, as the launcher will use it: a fully-qualified path is kept;
    /// a bare file name ("monero-wallet-rpc") is looked up on PATH like a shell would. Anything else
    /// (a relative path, or a name PATH doesn't have) comes back unchanged, so
    /// <see cref="EnsureLaunchable"/> refuses it with the user's own text in the message.
    /// </summary>
    public static string ResolveConfigured(string? configured, string? pathVariable)
    {
        string s = (configured ?? string.Empty).Trim().Trim('"');
        if (s.Length == 0 || Path.IsPathFullyQualified(s))
        {
            return s;
        }

        bool bareName = s.IndexOf('/') < 0 && s.IndexOf(Path.DirectorySeparatorChar) < 0;
        return (bareName ? FindOnPath(s, pathVariable) : null) ?? s;
    }

    /// <summary>
    /// Throws <see cref="FileNotFoundException"/> unless <paramref name="path"/> is a FULLY-QUALIFIED
    /// path to an existing file. Process.Start resolves anything else by searching — the app's own
    /// folder, then the CURRENT directory, then PATH — so a relative name could start a planted file
    /// from wherever the app happened to be launched, and that program would be handed the seed.
    /// </summary>
    public static void EnsureLaunchable(string? path)
    {
        string example = OperatingSystem.IsWindows() ? @"C:\monero\monero-wallet-rpc.exe" : "/usr/bin/monero-wallet-rpc";
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException(
                "monero-wallet-rpc wasn't found next to the app or on PATH. Install the official Monero CLI tools and set its full path in Settings.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new FileNotFoundException(
                $"'{path}' is not a full path. Set the full path to monero-wallet-rpc in Settings (for example {example}).", path);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"monero-wallet-rpc was not found at '{path}'. Download the official Monero CLI tools and set its path in Settings.", path);
        }
    }
}
