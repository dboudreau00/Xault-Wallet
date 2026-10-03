using System.Security.Cryptography;
using XaultWallet.Core.Diagnostics;

namespace XaultWallet.Core.Security;

/// <summary>
/// Best-effort overwrite-then-delete. On SSDs (wear levelling) and copy-on-write or journaling
/// filesystems the original blocks may survive — see SECURITY.md — but this removes the plaintext
/// from every ordinary recovery path. Never throws.
/// </summary>
internal static class SecureDelete
{
    /// <summary>Overwrite a file with random bytes, flush it to disk, then delete it.</summary>
    public static void File(string path)
    {
        try
        {
            var info = new FileInfo(path);
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

    /// <summary>Shred every file under <paramref name="dir"/>, then remove the directory.</summary>
    /// <returns>True when the directory is gone afterwards.</returns>
    public static bool Directory(string dir)
    {
        try
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
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
