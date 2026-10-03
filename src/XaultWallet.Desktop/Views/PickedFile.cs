using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop.Views;

/// <summary>Writes text to a file the user chose in a save dialog — owner-only (0600) where possible.</summary>
internal static class PickedFile
{
    public static async Task WriteTextAsync(IStorageFile file, string contents)
    {
        // With a local path the app creates the file 0600 itself. The dialog's own stream uses the
        // umask — commonly 0644, readable by every account on the machine — which is wrong for a
        // plaintext seed backup and needless for a transaction history.
        if (file.TryGetLocalPath() is { } path)
        {
            await Task.Run(() => PrivateFiles.WriteAllText(path, contents));
            return;
        }

        await using Stream stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(contents);
    }
}
