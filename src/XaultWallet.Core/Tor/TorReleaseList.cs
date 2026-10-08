using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XaultWallet.Core.Tor;

/// <summary>One Tor Expert Bundle as Tor Project's SIGNED checksum list names it.</summary>
/// <param name="Platform">Tor Project's platform name, e.g. "windows-x86_64".</param>
/// <param name="Version">The Tor Browser release it belongs to, e.g. "15.0.24".</param>
/// <param name="FileName">e.g. "tor-expert-bundle-windows-x86_64-15.0.24.tar.gz".</param>
/// <param name="Sha256">64 lower-case hex digits, from the signed list.</param>
public sealed record TorBundleArchive(string Platform, string Version, string FileName, string Sha256);

/// <summary>
/// Reads what Tor Project publishes about its releases. Two inputs, trusted differently:
/// <list type="bullet">
/// <item>the update channel's download-*.json only says which version is current. It is not signed,
/// so it only picks a folder to look in; the version must also be a plain number, never more than
/// that (it ends up in a URL and a folder name);</item>
/// <item>sha256sums-signed-build.txt is checked against its detached signature by the caller BEFORE
/// it reaches <see cref="Select"/>, and is the only source of the archive's checksum. Its file names
/// carry the version, so a list from another release can't be passed off as this one.</item>
/// </list>
/// </summary>
public static partial class TorReleaseList
{
    /// <summary>Tor Project's platform name for this machine, or null when it publishes no Expert
    /// Bundle for it.</summary>
    public static string? CurrentPlatform()
    {
        Architecture arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows() && arch == Architecture.X64)
        {
            return "windows-x86_64";
        }

        if (OperatingSystem.IsLinux() && arch == Architecture.X64)
        {
            return "linux-x86_64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return arch switch
            {
                Architecture.Arm64 => "macos-aarch64",
                Architecture.X64 => "macos-x86_64",
                _ => null,
            };
        }

        return null;
    }

    /// <summary>The update channel's JSON for a platform: macOS has one universal entry.</summary>
    public static string UpdateJsonName(string platform) =>
        platform.StartsWith("macos", StringComparison.Ordinal) ? "download-macos.json" : $"download-{platform}.json";

    /// <summary>The "version" of an update-channel download-*.json.</summary>
    /// <exception cref="InvalidOperationException">No usable version in it.</exception>
    public static string ParseCurrentVersion(string json)
    {
        string? version;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            version = doc.RootElement.ValueKind == JsonValueKind.Object
                      && doc.RootElement.TryGetProperty("version", out JsonElement v)
                      && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            version = null;
        }

        if (version is null || !IsPlainVersion(version))
        {
            throw new InvalidOperationException("Tor Project's update channel didn't name a release version this app understands.");
        }

        return version;
    }

    /// <summary>"15.0.24": two to four dot-separated numbers, nothing else (no alphas, no paths).</summary>
    public static bool IsPlainVersion(string version) => PlainVersion().IsMatch(version);

    /// <summary>The Expert Bundle archive name for a platform and version.</summary>
    public static string BundleFileName(string platform, string version) => $"tor-expert-bundle-{platform}-{version}.tar.gz";

    /// <summary>
    /// The Expert Bundle for <paramref name="platform"/> in a signed checksum list (already verified),
    /// which must be for <paramref name="version"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The list has no such bundle, or names it twice.</exception>
    public static TorBundleArchive Select(IEnumerable<string> signedLines, string platform, string version)
    {
        string fileName = BundleFileName(platform, version);
        var matches = new List<string>();
        foreach (string raw in signedLines)
        {
            Match m = SumsLine().Match(raw.TrimEnd('\r'));
            if (m.Success && m.Groups["name"].Value == fileName)
            {
                matches.Add(m.Groups["hash"].Value.ToLowerInvariant());
            }
        }

        return matches.Count switch
        {
            0 => throw new InvalidOperationException($"Tor Project's signed checksum list for {version} has no Tor Expert Bundle for this system ({platform})."),
            > 1 => throw new InvalidOperationException($"Tor Project's signed checksum list names the {platform} bundle more than once."),
            _ => new TorBundleArchive(platform, version, fileName, matches[0]),
        };
    }

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainVersion();

    // "<sha256>  <file>" (sha256sum's format; the second space may be "*" for binary mode).
    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64}) [ *](?<name>[A-Za-z0-9._-]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SumsLine();
}
