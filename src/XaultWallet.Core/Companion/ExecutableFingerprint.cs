using System.Security.Cryptography;

namespace XaultWallet.Core.Companion;

/// <summary>How a program file a browser connection leads to compares with the app that is running.</summary>
public enum FingerprintMatch
{
    /// <summary>Couldn't tell (a file couldn't be read): say nothing, never block anything.</summary>
    Unknown,

    /// <summary>The same bytes as the running app had when it started.</summary>
    Same,

    /// <summary>Different bytes.</summary>
    Different,
}

/// <summary>
/// SHA-256 of a program file, for the registration tripwire: the path a browser connection names is
/// only half the story, the file there could have been replaced since the app started. Release builds
/// aren't Authenticode-signed (RELEASE.md: verify against SHA256SUMS), so a signature can't be compared;
/// the running app's own file, hashed at start, is the reference.
/// </summary>
public static class ExecutableFingerprint
{
    /// <summary>Larger than any XaultWallet build: a bigger file isn't read (and can't be the app).</summary>
    public const long MaxBytes = 1L << 30;

    /// <summary>The file's SHA-256 (lowercase hex), or null when it isn't a readable regular file of a
    /// sensible size. Never throws.</summary>
    public static string? Sha256(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > MaxBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Compare a file's hash with the running app's (either may be unknown).</summary>
    public static FingerprintMatch Compare(string? runningApp, string? file) =>
        runningApp is null || file is null ? FingerprintMatch.Unknown
        : string.Equals(runningApp, file, StringComparison.Ordinal) ? FingerprintMatch.Same
        : FingerprintMatch.Different;
}
