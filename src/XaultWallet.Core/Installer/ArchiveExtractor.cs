using System.Formats.Tar;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;

namespace XaultWallet.Core.Installer;

/// <summary>
/// Copies ONE named file out of a Monero CLI archive (.zip on Windows, .tar.bz2 elsewhere). The
/// archive has already been matched against the signed checksum, so its content is exactly what
/// Monero published; this still never uses a path from inside the archive to decide where to write
/// (only the file's own name is compared), and caps how much it will write.
/// </summary>
internal static class ArchiveExtractor
{
    /// <summary>monero-wallet-rpc is a few tens of MB; anything this big is not it.</summary>
    internal const long MaxFileBytes = 512L * 1024 * 1024;

    /// <summary>Extract the first entry whose file name is <paramref name="fileName"/> to
    /// <paramref name="destination"/> (created; must not exist).</summary>
    /// <exception cref="InvalidOperationException">The archive has no such file, or it is too large.</exception>
    public static void ExtractFile(string archivePath, string fileName, string destination, CancellationToken ct)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractFromZip(archivePath, fileName, destination, ct);
        }
        else if (archivePath.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase))
        {
            ExtractFromTarBz2(archivePath, fileName, destination, ct);
        }
        else
        {
            throw new InvalidOperationException("Unsupported archive format.");
        }
    }

    private static void ExtractFromZip(string archivePath, string fileName, string destination, CancellationToken ct)
    {
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e => NameOf(e.FullName) == fileName);
        if (entry is null)
        {
            throw new InvalidOperationException($"{fileName} is not in the downloaded archive.");
        }

        if (entry.Length > MaxFileBytes)
        {
            throw new InvalidOperationException($"{fileName} in the archive is unexpectedly large.");
        }

        using Stream source = entry.Open();
        CopyCapped(source, destination, ct);
    }

    private static void ExtractFromTarBz2(string archivePath, string fileName, string destination, CancellationToken ct)
    {
        using FileStream file = File.OpenRead(archivePath);
        using var bzip2 = new BZip2InputStream(file);
        using var tar = new TarReader(bzip2);
        while (tar.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                && NameOf(entry.Name) == fileName
                && entry.DataStream is { } data)
            {
                if (entry.Length > MaxFileBytes)
                {
                    throw new InvalidOperationException($"{fileName} in the archive is unexpectedly large.");
                }

                CopyCapped(data, destination, ct);
                return;
            }
        }

        throw new InvalidOperationException($"{fileName} is not in the downloaded archive.");
    }

    /// <summary>The last path component, whichever separator the archive used.</summary>
    private static string NameOf(string entryPath)
    {
        int slash = Math.Max(entryPath.LastIndexOf('/'), entryPath.LastIndexOf('\\'));
        return slash < 0 ? entryPath : entryPath[(slash + 1)..];
    }

    private static void CopyCapped(Stream source, string destination, CancellationToken ct)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += read;
            if (total > MaxFileBytes)
            {
                throw new InvalidOperationException("The file in the archive is unexpectedly large.");
            }

            output.Write(buffer, 0, read);
        }

        output.Flush(flushToDisk: true);
    }
}
