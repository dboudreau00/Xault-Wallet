using System.Text;
using XaultWallet.Core.Security;

namespace XaultWallet.Core.Companion;

/// <summary>
/// Makes this copy of XaultWallet the program that opens monero: links, for the current user only: a
/// URL protocol under HKCU\Software\Classes on Windows, a desktop entry and a mimeapps.list default on
/// Linux. A link then starts XaultWallet with the link as its argument (or hands it to the copy already
/// running), and the payment request waits in the Send tab for the user to review. Not available on
/// macOS, where the release is a plain executable rather than an app bundle.
/// </summary>
public static class MoneroLinkRegistration
{
    public const string Scheme = "monero";

    private const string ClassKey = @"Software\Classes\monero";
    private const string CommandKey = ClassKey + @"\shell\open\command";
    private const string UserChoiceKey = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\monero\UserChoice";
    private const string DesktopFileName = "xaultwallet-monero-link.desktop";
    private const string MimeType = "x-scheme-handler/monero";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>The command Windows runs for a link: the executable, quoted, with the link as its one argument.</summary>
    public static string WindowsCommand(string executable) => "\"" + executable + "\" \"%1\"";

    public static RegistrationState Status(string executable, BrowserPaths paths, IUserRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (OperatingSystem.IsWindows())
        {
            if (registry is null)
            {
                return RegistrationState.Unsupported;
            }

            string? command = registry.GetValue(CommandKey, string.Empty);
            if (command is null)
            {
                return RegistrationState.NotRegistered;
            }

            return string.Equals(command, WindowsCommand(executable), StringComparison.OrdinalIgnoreCase) ? RegistrationState.Registered : RegistrationState.Elsewhere;
        }

        if (OperatingSystem.IsLinux())
        {
            string? handler = DefaultHandler(paths);
            if (handler is null)
            {
                return RegistrationState.NotRegistered;
            }

            if (handler != DesktopFileName)
            {
                return RegistrationState.Elsewhere;
            }

            string desktop = DesktopFilePath(paths);
            return File.Exists(desktop) && File.ReadAllText(desktop).Contains("\nExec=" + DesktopExec(executable) + "\n", StringComparison.Ordinal)
                ? RegistrationState.Registered
                : RegistrationState.Elsewhere;
        }

        return RegistrationState.Unsupported;
    }

    /// <summary>On Windows: true when the user picked another app for monero: links in Windows' own
    /// "default apps" settings, which outranks any registration made here.</summary>
    public static bool WindowsUserChoiceOverrides(IUserRegistry? registry) =>
        OperatingSystem.IsWindows() && registry?.GetValue(UserChoiceKey, "ProgId") is { Length: > 0 } progId
        && !string.Equals(progId, Scheme, StringComparison.OrdinalIgnoreCase);

    public static void Register(string executable, BrowserPaths paths, IUserRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("The executable must be a full path.", nameof(executable));
        }

        if (OperatingSystem.IsWindows())
        {
            if (registry is null)
            {
                throw new PlatformNotSupportedException("Opening monero: links needs the registry on Windows.");
            }

            // A protocol another program registered for this user is replaced: the user asked for it.
            registry.DeleteTree(ClassKey);
            registry.SetValue(ClassKey, string.Empty, "URL:Monero payment link");
            registry.SetValue(ClassKey, "URL Protocol", string.Empty);
            registry.SetValue(ClassKey + @"\DefaultIcon", string.Empty, "\"" + executable + "\",0");
            registry.SetValue(CommandKey, string.Empty, WindowsCommand(executable));
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Opening monero: links from XaultWallet isn't available on this system.");
        }

        string desktop = DesktopFilePath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
        string entry = "[Desktop Entry]\nType=Application\nName=XaultWallet\nComment=Pay monero: links with XaultWallet\n" +
                       "Exec=" + DesktopExec(executable) + "\nTerminal=false\nNoDisplay=true\nMimeType=" + MimeType + ";\n";
        WriteAtomically(desktop, entry);
        SetDefaultHandler(paths, DesktopFileName);
    }

    /// <summary>Undo <see cref="Register"/>; a registration that belongs to another program is left alone.</summary>
    public static void Unregister(string executable, BrowserPaths paths, IUserRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (OperatingSystem.IsWindows())
        {
            if (registry is not null && Status(executable, paths, registry) == RegistrationState.Registered)
            {
                registry.DeleteTree(ClassKey);
            }

            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (DefaultHandler(paths) == DesktopFileName)
        {
            SetDefaultHandler(paths, null);
        }

        string desktop = DesktopFilePath(paths);
        if (File.Exists(desktop))
        {
            File.Delete(desktop);
        }
    }

    internal static string DesktopFilePath(BrowserPaths paths) => Path.Combine(paths.DataHome, "applications", DesktopFileName);

    internal static string MimeAppsPath(BrowserPaths paths) => Path.Combine(paths.ConfigHome, "mimeapps.list");

    /// <summary>The Exec line's command: the executable, quoted as the desktop entry format asks, then
    /// the link. A path with characters that need escaping inside quotes is refused instead.</summary>
    internal static string DesktopExec(string executable)
    {
        if (executable.IndexOfAny(['"', '`', '$', '\\', '%', '\n', '\r']) >= 0)
        {
            throw new ArgumentException("XaultWallet's path has a character a desktop entry can't hold (\" ` $ \\ %). Move it to a plainer folder.", nameof(executable));
        }

        return "\"" + executable + "\" %u";
    }

    /// <summary>The desktop file the user's mimeapps.list names for monero: links, if any.</summary>
    internal static string? DefaultHandler(BrowserPaths paths)
    {
        string list = MimeAppsPath(paths);
        if (!File.Exists(list))
        {
            return null;
        }

        bool inDefaults = false;
        foreach (string raw in File.ReadLines(list))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inDefaults = line == "[Default Applications]";
                continue;
            }

            if (inDefaults && line.StartsWith(MimeType + "=", StringComparison.Ordinal))
            {
                string value = line[(MimeType.Length + 1)..].Trim();
                string first = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
                return first.Length > 0 ? first : null;
            }
        }

        return null;
    }

    /// <summary>Set (or with null, remove) the monero: default in mimeapps.list, keeping every other
    /// line of the file as it was.</summary>
    internal static void SetDefaultHandler(BrowserPaths paths, string? desktopFile)
    {
        string list = MimeAppsPath(paths);
        List<string> lines = File.Exists(list) ? File.ReadAllLines(list).ToList() : new List<string>();
        int section = lines.FindIndex(l => l.Trim() == "[Default Applications]");
        if (section < 0)
        {
            if (desktopFile is null)
            {
                return;
            }

            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("[Default Applications]");
            section = lines.Count - 1;
        }

        int end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0)
        {
            end = lines.Count;
        }

        int existing = lines.FindIndex(section + 1, end - section - 1, l => l.Trim().StartsWith(MimeType + "=", StringComparison.Ordinal));
        if (desktopFile is null)
        {
            if (existing >= 0)
            {
                lines.RemoveAt(existing);
            }
        }
        else if (existing >= 0)
        {
            lines[existing] = MimeType + "=" + desktopFile + ";";
        }
        else
        {
            lines.Insert(section + 1, MimeType + "=" + desktopFile + ";");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        WriteAtomically(list, string.Join('\n', lines) + "\n");
    }

    private static void WriteAtomically(string path, string text)
    {
        string tmp = path + ".xaultwallet.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
