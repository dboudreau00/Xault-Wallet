using System.Text;

namespace XaultWallet.Core.Security;

/// <summary>
/// Files and folders holding wallet material are created readable by this user only: 0600 files and
/// 0700 folders on Linux/macOS. Without this they inherit the process umask — commonly 022, i.e.
/// world-readable 0644 files — so on a shared machine any local account could read the vault (and
/// brute-force it offline), the logs, or a plaintext seed backup. On Windows the per-user profile
/// ACLs already restrict these locations; nothing is changed there.
/// </summary>
public static class PrivateFiles
{
    public const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode OwnerDirectory = OwnerFile | UnixFileMode.UserExecute;

    /// <summary>Create <paramref name="path"/> as a 0700 folder, or tighten an existing one to 0700.</summary>
    public static string EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return path;
        }

        Directory.CreateDirectory(path, OwnerDirectory); // the mode only applies when it is created...
        File.SetUnixFileMode(path, OwnerDirectory);       // ...so tighten one an older version made
        return path;
    }

    /// <summary>
    /// Open <paramref name="path"/> for writing from scratch, as a 0600 file. An existing file is
    /// tightened BEFORE anything is written into it (a create mode only applies to new files).
    /// </summary>
    public static FileStream OpenWrite(string path, FileMode mode = FileMode.Create)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFile;
            Tighten(path);
        }

        return new FileStream(path, options);
    }

    /// <summary>Write <paramref name="bytes"/> to a 0600 file and flush them to disk.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using FileStream fs = OpenWrite(path);
        fs.Write(bytes);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>UTF-8 (no BOM) text to a 0600 file.</summary>
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Append UTF-8 text, creating the file as 0600 if it does not exist (no chmod per call).</summary>
    public static void AppendAllText(string path, string text)
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFile;
        }

        using var fs = new FileStream(path, options);
        fs.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Best effort: make an existing file 0600. Never follows a symlink.</summary>
    public static void Tighten(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.LinkTarget is null)
            {
                File.SetUnixFileMode(path, OwnerFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: the caller's write still proceeds (and fails loudly if it must)
        }
    }

    /// <summary>
    /// Startup sweep for installs made before this rule: <paramref name="directory"/> becomes 0700 and
    /// everything below it 0600 (files) / 0700 (folders). Symlinks are neither followed nor changed.
    /// </summary>
    public static void TightenTree(string directory)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null)
        {
            return;
        }

        IEnumerable<FileSystemInfo> entries;
        try
        {
            File.SetUnixFileMode(directory, OwnerDirectory);
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (FileSystemInfo entry in entries)
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            if (entry is DirectoryInfo sub)
            {
                TightenTree(sub.FullName);
                continue;
            }

            try
            {
                File.SetUnixFileMode(entry.FullName, OwnerFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // one unchangeable file must not stop the rest being tightened
            }
        }
    }
}
