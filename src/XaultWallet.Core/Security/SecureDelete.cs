using System.Security.Cryptography;
using XaultWallet.Core.Diagnostics;

namespace XaultWallet.Core.Security;

/// <summary>
/// Best-effort overwrite-then-delete. On SSDs (wear levelling) and copy-on-write or journaling
/// filesystems the original blocks may survive — see SECURITY.md — but this removes the plaintext
/// from every ordinary recovery path. Never throws. Never FOLLOWS a link: a symlink or reparse point
/// is removed as a link and its target is left alone, so a planted link in a temp folder cannot make
/// the shredder overwrite a file elsewhere.
/// </summary>
internal static class SecureDelete
{
    /// <summary>Overwrite a file with random bytes, flush it to disk, then delete it.</summary>
    public static void File(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (IsLink(info))
            {
                System.IO.File.Delete(path); // removes the link itself, never its target
                return;
            }

            if (info.Exists && info.Length > 0)
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                byte[] noise = new byte[81920];
                long remaining = info.Length;
                while (remaining > 0)
                {
                    RandomNumberGenerator.Fill(noise);
                    int chunk = (int)Math.Min(noise.Length, remaining);
                    fs.Write(noise, 0, chunk);
                    remaining -= chunk;
                }

                fs.Flush(flushToDisk: true);
            }

            System.IO.File.Delete(path);
        }
        catch
        {
            // best effort; a parent-directory delete may still remove it
        }
    }

    /// <summary>True for a symbolic link, junction or other reparse point (whatever it points at).</summary>
    internal static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true; // can't tell: treat as a link and don't touch what it might lead to
        }
    }

    /// <summary>Shred every file under <paramref name="dir"/>, then remove the directory. Links are
    /// neither entered nor overwritten; a link given as <paramref name="dir"/> is just removed.</summary>
    /// <returns>True when the directory is gone afterwards.</returns>
    public static bool Directory(string dir)
    {
        try
        {
            var root = new DirectoryInfo(dir);
            if (IsLink(root))
            {
                root.Delete(recursive: false); // the link only
                return !root.Exists;
            }

            // AttributesToSkip = ReparsePoint hides symlinked files and folders (Unix symlinks count as
            // reparse points to .NET) and stops the recursion at them; Directory.Delete below removes a
            // link inside the tree as a link, without entering it.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            };
            foreach (string file in System.IO.Directory.EnumerateFiles(dir, "*", options))
            {
                File(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Error enumerating a directory for shredding: {ex.GetType().Name}");
        }

        try
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort
        }

        return !System.IO.Directory.Exists(dir);
    }
}
