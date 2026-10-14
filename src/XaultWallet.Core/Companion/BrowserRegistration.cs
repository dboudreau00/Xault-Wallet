using System.Buffers;
using System.Text.Json;
using XaultWallet.Core.Security;

namespace XaultWallet.Core.Companion;

/// <summary>A browser the companion extension runs in.</summary>
public enum CompanionBrowser
{
    Chrome,
    Firefox,
}

/// <summary>Whether something points at this copy of XaultWallet.</summary>
public enum RegistrationState
{
    NotRegistered,

    /// <summary>Registered, for this copy of XaultWallet.</summary>
    Registered,

    /// <summary>Registered, but for another program or another copy of XaultWallet.</summary>
    Elsewhere,

    /// <summary>Not available on this operating system.</summary>
    Unsupported,
}

/// <summary>A registration's state, and the program it leads to when that could be read (as the
/// registration has it, written by whoever wrote it: clean it before showing it).</summary>
public sealed record RegistrationCheck(RegistrationState State, string? Target = null);

/// <summary>
/// The folders the browsers look in for a native messaging host. Passed in, rather than read here,
/// so tests can point them at a throwaway folder.
/// </summary>
/// <param name="Home">The user's home folder.</param>
/// <param name="ConfigHome">$XDG_CONFIG_HOME or ~/.config (Linux).</param>
/// <param name="DataHome">$XDG_DATA_HOME or ~/.local/share (Linux).</param>
/// <param name="AppLocalData">Where XaultWallet keeps machine-local files (%LOCALAPPDATA%\XaultWallet on Windows).</param>
public sealed record BrowserPaths(string Home, string ConfigHome, string DataHome, string AppLocalData)
{
    /// <summary>The current user's folders.</summary>
    public static BrowserPaths ForCurrentUser(string appLocalData)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c && Path.IsPathFullyQualified(c) ? c : Path.Combine(home, ".config");
        string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d && Path.IsPathFullyQualified(d) ? d : Path.Combine(home, ".local", "share");
        return new BrowserPaths(home, config, data, appLocalData);
    }
}

/// <summary>The current user's registry hive (HKEY_CURRENT_USER), as far as registration needs it.</summary>
public interface IUserRegistry
{
    /// <summary>A value of <paramref name="subKey"/> ("" is the default value); null when absent.</summary>
    string? GetValue(string subKey, string name);

    void SetValue(string subKey, string name, string value);

    /// <summary>Delete the key and everything under it; nothing happens when it doesn't exist.</summary>
    void DeleteTree(string subKey);
}

/// <summary>HKEY_CURRENT_USER itself.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsUserRegistry : IUserRegistry
{
    /// <summary>The value as text; null when absent. A value of another kind (a number, binary data, a
    /// list) throws <see cref="InvalidDataException"/>: something is there that this app didn't write.</summary>
    public string? GetValue(string subKey, string name)
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key?.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            null => null,
            string text => text,
            _ => throw new InvalidDataException("The registry value isn't text."),
        };
    }

    public void SetValue(string subKey, string name, string value)
    {
        using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
    }

    public void DeleteTree(string subKey) => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
}

/// <summary>
/// Connects (and disconnects) Chrome and Firefox to this copy of XaultWallet: a native messaging host
/// manifest naming the executable and the one extension each browser may let talk to it, in the place
/// that browser looks (a file, plus a registry key on Windows). Only the current user is changed;
/// nothing is done until the user asks for it in Settings.
/// </summary>
public static class BrowserRegistration
{
    /// <summary>The native messaging host's name, as the extension asks for it.</summary>
    public const string HostName = "dev.dboudreau.xaultwallet";

    /// <summary>
    /// The Chrome extension ids allowed to talk to the app. The id is derived from the public key in the
    /// extension's manifest; the matching private key was never kept, so no store listing can claim it.
    /// A store listing gets an id of its own, added here in a release.
    /// <para>The key is public (it is in the manifest), so this id is no secret: any unpacked extension that
    /// copies the key gets the same id, and Chrome lets it use this host. The id check keeps other web-store
    /// extensions out, not a copy someone loads unpacked (SECURITY.md, "Browser companion").</para>
    /// TODO: once the Chrome Web Store listing exists, allow only its store-assigned id here (drop the
    /// manifest-key id, or keep it only in a developer build) so a copied key no longer passes; build.mjs
    /// then has to stop requiring the manifest key's id. No store id exists yet; don't add one until it does.
    /// </summary>
    public static readonly IReadOnlyList<string> ChromeExtensionIds = ["kpcmnhbbahdifkhgibnboafelgfkgcdj"];

    /// <summary>The Firefox extension ids allowed to talk to the app (from the extension's manifest).</summary>
    public static readonly IReadOnlyList<string> FirefoxExtensionIds = ["xaultwallet-companion@dboudreau.dev"];

    private const string Description = "XaultWallet browser companion";

    /// <summary>The host manifest for <paramref name="browser"/>, for the executable at <paramref name="executable"/>.</summary>
    public static string ManifestJson(CompanionBrowser browser, string executable)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("name", HostName);
            w.WriteString("description", Description);
            w.WriteString("path", executable);
            w.WriteString("type", "stdio");
            if (browser == CompanionBrowser.Chrome)
            {
                w.WriteStartArray("allowed_origins");
                foreach (string id in ChromeExtensionIds)
                {
                    w.WriteStringValue("chrome-extension://" + id + "/");
                }
            }
            else
            {
                w.WriteStartArray("allowed_extensions");
                foreach (string id in FirefoxExtensionIds)
                {
                    w.WriteStringValue(id);
                }
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    /// <summary>Where <paramref name="browser"/> looks for the manifest (on Windows: where this app keeps
    /// it; the registry key names it).</summary>
    public static string? ManifestPath(CompanionBrowser browser, BrowserPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string file = HostName + ".json";
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(paths.AppLocalData, "browser", HostName + "." + (browser == CompanionBrowser.Chrome ? "chrome" : "firefox") + ".json");
        }

        if (OperatingSystem.IsMacOS())
        {
            string support = Path.Combine(paths.Home, "Library", "Application Support");
            return browser == CompanionBrowser.Chrome
                ? Path.Combine(support, "Google", "Chrome", "NativeMessagingHosts", file)
                : Path.Combine(support, "Mozilla", "NativeMessagingHosts", file);
        }

        if (OperatingSystem.IsLinux())
        {
            return browser == CompanionBrowser.Chrome
                ? Path.Combine(paths.ConfigHome, "google-chrome", "NativeMessagingHosts", file)
                : Path.Combine(paths.Home, ".mozilla", "native-messaging-hosts", file);
        }

        return null;
    }

    /// <summary>The HKCU key that names the manifest (Windows only).</summary>
    public static string RegistryKey(CompanionBrowser browser) =>
        (browser == CompanionBrowser.Chrome ? @"Software\Google\Chrome\NativeMessagingHosts\" : @"Software\Mozilla\NativeMessagingHosts\") + HostName;

    public static RegistrationState Status(CompanionBrowser browser, string executable, BrowserPaths paths, IUserRegistry? registry) =>
        Check(browser, executable, paths, registry).State;

    /// <summary>
    /// Where <paramref name="browser"/>'s registration leads: its state, and the program its manifest names
    /// when that could be read (as written there, by whoever wrote it: clean it before showing it). Never
    /// throws: what can't be read counts as leading elsewhere.
    /// </summary>
    public static RegistrationCheck Check(CompanionBrowser browser, string executable, BrowserPaths paths, IUserRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            string? manifest = ManifestPath(browser, paths);
            if (manifest is null)
            {
                return new RegistrationCheck(RegistrationState.Unsupported);
            }

            if (OperatingSystem.IsWindows())
            {
                if (registry is null)
                {
                    return new RegistrationCheck(RegistrationState.Unsupported);
                }

                // The key decides which manifest the browser reads, wherever it is. This app writes a full
                // local path; a relative, unexpanded or network one is someone else's.
                string? named = registry.GetValue(RegistryKey(browser), string.Empty);
                if (string.IsNullOrEmpty(named))
                {
                    return new RegistrationCheck(RegistrationState.NotRegistered);
                }

                if (!Path.IsPathFullyQualified(named) || named.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    return new RegistrationCheck(RegistrationState.Elsewhere);
                }

                manifest = named;
            }

            var info = new FileInfo(manifest);
            if (info.LinkTarget is not null)
            {
                return new RegistrationCheck(RegistrationState.Elsewhere); // this app writes a plain file, never a link
            }

            if (!info.Exists)
            {
                return new RegistrationCheck(RegistrationState.NotRegistered);
            }

            string? path = ReadPath(info);
            return path is null
                ? new RegistrationCheck(RegistrationState.Elsewhere)
                : new RegistrationCheck(SamePath(path, executable) ? RegistrationState.Registered : RegistrationState.Elsewhere, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RegistrationCheck(RegistrationState.Elsewhere); // unreadable: assume the worst
        }
    }

    /// <summary>Point <paramref name="browser"/> at <paramref name="executable"/>, replacing any earlier
    /// registration (of this or another copy).</summary>
    public static void Register(CompanionBrowser browser, string executable, BrowserPaths paths, IUserRegistry? registry)
    {
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("The executable must be a full path.", nameof(executable));
        }

        string manifest = ManifestPath(browser, paths) ?? throw new PlatformNotSupportedException("The browser companion isn't available on this system.");
        if (OperatingSystem.IsWindows() && registry is null)
        {
            throw new PlatformNotSupportedException("The browser companion needs the registry on Windows.");
        }

        string directory = Path.GetDirectoryName(manifest)!;
        Directory.CreateDirectory(directory);
        string tmp = manifest + ".tmp";
        PrivateFiles.WriteAllText(tmp, ManifestJson(browser, executable));
        File.Move(tmp, manifest, overwrite: true);

        if (OperatingSystem.IsWindows())
        {
            registry!.SetValue(RegistryKey(browser), string.Empty, manifest);
        }
    }

    /// <summary>Remove <paramref name="browser"/>'s registration and this app's manifest for it.</summary>
    public static void Unregister(CompanionBrowser browser, BrowserPaths paths, IUserRegistry? registry)
    {
        if (OperatingSystem.IsWindows())
        {
            registry?.DeleteTree(RegistryKey(browser));
        }

        if (ManifestPath(browser, paths) is { } manifest && File.Exists(manifest))
        {
            File.Delete(manifest);
        }

        // On Windows the manifests live in this app's own folder: once the last one is gone, so is the
        // folder (an uninstall leaves nothing behind). Elsewhere the folder is the browser's: left alone.
        if (OperatingSystem.IsWindows() && ManifestPath(browser, paths) is { } own && Path.GetDirectoryName(own) is { } folder
            && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
        }
    }

    private const int MaxManifestBytes = 64 * 1024;

    /// <summary>The "path" of a host manifest; null when it isn't a manifest. Only a regular file of a
    /// sensible size is opened (a pipe or a device reports no size and is left alone), and no more than
    /// that is read. Throws when the file can't be read.</summary>
    internal static string? ReadPath(FileInfo manifest)
    {
        if (manifest.Length is 0 or > MaxManifestBytes)
        {
            return null;
        }

        byte[] buffer = new byte[MaxManifestBytes + 1];
        int total = 0;
        using (var stream = new FileStream(manifest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }
        }

        if (total > MaxManifestBytes)
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.AsMemory(0, total));
        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }

    /// <summary>Whether a manifest's "path" names exactly this executable, compared as written, which is
    /// how the browser uses it. A relative path (Windows browsers resolve it against the manifest's folder),
    /// or any roundabout spelling of the same file, counts as another program.</summary>
    internal static bool SamePath(string path, string executable) =>
        Path.IsPathFullyQualified(path)
        && string.Equals(path, executable, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
