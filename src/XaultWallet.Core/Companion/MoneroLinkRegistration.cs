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
    private const int MaxListBytes = 1024 * 1024;

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>The command Windows runs for a link: the executable, quoted, with the link as its one argument.</summary>
    public static string WindowsCommand(string executable) => "\"" + executable + "\" \"%1\"";

    /// <summary>Whether this copy opens monero: links. Never throws: what can't be read counts as
    /// another program's.</summary>
    public static RegistrationState Status(string executable, BrowserPaths paths, IUserRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
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

                if (!string.Equals(command, WindowsCommand(executable), StringComparison.OrdinalIgnoreCase))
                {
                    return RegistrationState.Elsewhere;
                }

                // Other ways the shell could run something else for the same links: a COM handler, DDE, or
                // another verb made the default. This app writes none of them.
                return registry.GetValue(CommandKey, "DelegateExecute") is not null
                       || registry.GetValue(ClassKey + @"\shell\open\ddeexec", string.Empty) is not null
                       || (registry.GetValue(ClassKey + @"\shell", string.Empty) is { Length: > 0 } verb && !string.Equals(verb, "open", StringComparison.OrdinalIgnoreCase))
                    ? RegistrationState.Elsewhere
                    : RegistrationState.Registered;
            }

            if (OperatingSystem.IsLinux())
            {
                string? handler = DefaultHandler(paths);
                if (handler is null)
                {
                    return RegistrationState.NotRegistered;
                }

                return handler == DesktopFileName && IsOurDesktopFile(paths, executable) ? RegistrationState.Registered : RegistrationState.Elsewhere;
            }

            return RegistrationState.Unsupported;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return RegistrationState.Elsewhere;
        }
    }

    /// <summary>What opens monero: links now, as the registration has it (the command on Windows, the
    /// desktop file on Linux), to say what a change replaces; null when nothing does or it can't be read.
    /// Written by whoever registered it: clean it before showing it.</summary>
    public static string? CurrentHandler(BrowserPaths paths, IUserRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            return OperatingSystem.IsWindows() ? registry?.GetValue(CommandKey, string.Empty)
                : OperatingSystem.IsLinux() ? DefaultHandler(paths)
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>On Windows: true when the user picked another app for monero: links in Windows' own
    /// "default apps" settings, which outranks any registration made here (and when that choice can't
    /// be read).</summary>
    public static bool WindowsUserChoiceOverrides(IUserRegistry? registry)
    {
        if (!OperatingSystem.IsWindows() || registry is null)
        {
            return false;
        }

        try
        {
            return registry.GetValue(UserChoiceKey, "ProgId") is { Length: > 0 } progId
                   && !string.Equals(progId, Scheme, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }

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

            // The command's "%1" is the link: a path holding % or a quote could be rewritten by it.
            if (executable.IndexOfAny(['%', '"']) >= 0)
            {
                throw new ArgumentException("XaultWallet's path has a character a link command can't hold (% or \"). Move it to a plainer folder.", nameof(executable));
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

    /// <summary>Undo <see cref="Register"/>. A registration that belongs to another program, or to
    /// another copy of XaultWallet, is left alone.</summary>
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

        // The desktop file is this copy's only when it runs this executable; the default naming it goes with it.
        if (!OperatingSystem.IsLinux() || !IsOurDesktopFile(paths, executable))
        {
            return;
        }

        if (DefaultHandler(paths) == DesktopFileName)
        {
            SetDefaultHandler(paths, null);
        }

        File.Delete(DesktopFilePath(paths));
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

    /// <summary>Whether the desktop file this app writes is there, a plain file, and its [Desktop Entry]
    /// runs <paramref name="executable"/> (one Exec key, exactly this app's command).</summary>
    private static bool IsOurDesktopFile(BrowserPaths paths, string executable)
    {
        string expected;
        try
        {
            expected = DesktopExec(executable);
        }
        catch (ArgumentException)
        {
            return false; // this app never writes one for such a path
        }

        var info = new FileInfo(DesktopFilePath(paths));
        if (!info.Exists || info.LinkTarget is not null || info.Length is 0 or > 64 * 1024)
        {
            return false;
        }

        var execs = new List<string>();
        bool inEntry = false;
        foreach (string raw in File.ReadLines(info.FullName))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEntry = line == "[Desktop Entry]";
                continue;
            }

            if (inEntry && KeyOf(line) == "Exec")
            {
                execs.Add(ValueOf(line));
            }
        }

        return execs.Count == 1 && execs[0] == expected;
    }

    /// <summary>The desktop file the user's mimeapps.list names for monero: links, if any.</summary>
    internal static string? DefaultHandler(BrowserPaths paths)
    {
        var list = new FileInfo(MimeAppsPath(paths));
        if (!list.Exists || list.Length is 0 or > MaxListBytes)
        {
            return null; // nothing there (a pipe or a device reports no size and is never opened)
        }

        bool inDefaults = false;
        foreach (string raw in File.ReadLines(list.FullName))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inDefaults = line == "[Default Applications]";
                continue;
            }

            if (inDefaults && KeyOf(line) == MimeType)
            {
                string first = ValueOf(line).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
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
        var info = new FileInfo(list);
        if (info.Exists && info.Length > MaxListBytes)
        {
            throw new IOException("mimeapps.list is too large to edit.");
        }

        List<string> lines = info.Exists && info.Length > 0 ? File.ReadAllLines(list).ToList() : new List<string>();
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

        // Every monero: line of the section goes ("key = value" spacing included); one is written back.
        for (int i = end - 1; i > section; i--)
        {
            if (KeyOf(lines[i].Trim()) == MimeType)
            {
                lines.RemoveAt(i);
            }
        }

        if (desktopFile is not null)
        {
            lines.Insert(section + 1, MimeType + "=" + desktopFile + ";");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        WriteAtomically(list, string.Join('\n', lines) + "\n");
    }

    private static string? KeyOf(string line)
    {
        int eq = line.IndexOf('=');
        return eq > 0 ? line[..eq].Trim() : null;
    }

    private static string ValueOf(string line) => line[(line.IndexOf('=') + 1)..].Trim();

    /// <summary>Replace a file in one step, readable by this user only. A file kept as a link (a dotfiles
    /// setup) is written where the link leads, and stays a link.</summary>
    private static void WriteAtomically(string path, string text)
    {
        var info = new FileInfo(path);
        string target = info.LinkTarget is not null ? info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path : path;
        string tmp = target + ".xaultwallet.tmp";
        PrivateFiles.WriteAllText(tmp, text);
        File.Move(tmp, target, overwrite: true);
    }
}
