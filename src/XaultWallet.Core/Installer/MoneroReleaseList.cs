using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace XaultWallet.Core.Installer;

/// <summary>One CLI archive named in the signed hash list.</summary>
/// <param name="FileName">e.g. "monero-win-x64-v0.18.5.1.zip".</param>
/// <param name="Platform">e.g. "win-x64", "linux-armv8", "mac-armv8".</param>
/// <param name="Version">e.g. "0.18.5.1".</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the archive.</param>
public sealed record MoneroCliArchive(string FileName, string Platform, string Version, string Sha256);

/// <summary>
/// Reads the CLI entries of Monero's signed hash list (getmonero.org/downloads/hashes.txt): lines
/// of the form "&lt;sha256&gt;  monero-&lt;platform&gt;-v&lt;version&gt;.(zip|tar.bz2)". Only ever give it
/// text that <see cref="OpenPgp.VerifyClearSigned"/> returned: the hashes are only as trustworthy
/// as the signature over them. Platforms start with an OS name Monero builds for, so GUI and source
/// entries ("monero-gui-…", "monero-source-…") don't match and are ignored.
/// </summary>
public static partial class MoneroReleaseList
{
    [GeneratedRegex(@"^(?<sha>[0-9a-f]{64})\s+(?<file>monero-(?<platform>(?:android|freebsd|linux|mac|win)-[a-z0-9]+)-v(?<version>[0-9]+(?:\.[0-9]+){2,3})\.(?:zip|tar\.bz2))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Entry();

    /// <summary>Every CLI archive listed in the (already verified) text.</summary>
    public static IReadOnlyList<MoneroCliArchive> Parse(IEnumerable<string> signedLines)
    {
        ArgumentNullException.ThrowIfNull(signedLines);
        var archives = new List<MoneroCliArchive>();
        foreach (string line in signedLines)
        {
            Match m = Entry().Match(line.Trim());
            if (m.Success)
            {
                archives.Add(new MoneroCliArchive(
                    m.Groups["file"].Value, m.Groups["platform"].Value, m.Groups["version"].Value, m.Groups["sha"].Value));
            }
        }

        return archives;
    }

    /// <summary>The archive for <paramref name="platform"/>.</summary>
    /// <exception cref="InvalidOperationException">The list has none for it, or more than one.</exception>
    public static MoneroCliArchive Select(IEnumerable<string> signedLines, string platform)
    {
        List<MoneroCliArchive> matches = Parse(signedLines).Where(a => a.Platform == platform).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"Monero's signed release list has no command-line build for this system ({platform})."),
            _ => throw new InvalidOperationException(
                $"Monero's signed release list names more than one build for {platform}; install monero-wallet-rpc yourself."),
        };
    }

    /// <summary>Monero's name for the platform this app runs on, or null when Monero publishes no
    /// command-line build for it. Windows on ARM uses the x64 build (Windows runs it emulated).</summary>
    public static string? CurrentPlatform() =>
        PlatformName(
            OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OperatingSystem.IsLinux() ? OSPlatform.Linux
            : OperatingSystem.IsFreeBSD() ? OSPlatform.FreeBSD
            : OSPlatform.Create("other"),
            RuntimeInformation.OSArchitecture);

    /// <summary>Monero's platform name for an OS and CPU, or null when it publishes no CLI build for them.</summary>
    public static string? PlatformName(OSPlatform os, Architecture arch)
    {
        if (os == OSPlatform.Windows)
        {
            return arch switch
            {
                Architecture.X64 or Architecture.Arm64 => "win-x64",
                Architecture.X86 => "win-x86",
                _ => null,
            };
        }

        if (os == OSPlatform.OSX)
        {
            return arch switch
            {
                Architecture.Arm64 => "mac-armv8",
                Architecture.X64 => "mac-x64",
                _ => null,
            };
        }

        if (os == OSPlatform.Linux)
        {
            return arch switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-armv8",
                Architecture.Arm => "linux-armv7",
                Architecture.X86 => "linux-x86",
                _ => null,
            };
        }

        if (os == OSPlatform.FreeBSD)
        {
            return arch == Architecture.X64 ? "freebsd-x64" : null;
        }

        return null;
    }

    /// <summary>The wallet-rpc file name inside a <paramref name="platform"/> archive.</summary>
    public static string WalletRpcFileName(string platform) =>
        platform.StartsWith("win-", StringComparison.Ordinal) ? "monero-wallet-rpc.exe" : "monero-wallet-rpc";
}
