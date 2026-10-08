using Avalonia.Input;
using Avalonia.Input.Platform;

namespace XaultWallet.Desktop;

/// <summary>
/// Clipboard writes for wallet data (addresses, keys, tx ids). Remembers the last value this app
/// put there so lock and exit can clear it without stomping something the user copied elsewhere.
/// On Windows, marks the payload to be excluded from clipboard history and cloud clipboard.
/// </summary>
internal static class SensitiveClipboard
{
    private static IClipboard? _last;
    private static string? _lastText;

    /// <summary>
    /// Windows's documented way to keep a copy out of clipboard history and the cloud clipboard
    /// ("ExcludeClipboardContentFromMonitorProcessing"). Platform formats: their names reach the
    /// OS as they are, and their values are written raw.
    /// </summary>
    private static readonly DataFormat<byte[]>[] WindowsExclusionFormats =
    [
        DataFormat.CreateBytesPlatformFormat("ExcludeClipboardContentFromMonitorProcessing"),
        DataFormat.CreateBytesPlatformFormat("CanIncludeInClipboardHistory"),
        DataFormat.CreateBytesPlatformFormat("CanUploadToCloudClipboard"),
    ];

    /// <summary>Put <paramref name="text"/> on the clipboard and remember it as ours.</summary>
    public static async Task SetAsync(IClipboard clipboard, string text)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(text);

        if (OperatingSystem.IsWindows())
        {
            var item = new DataTransferItem();
            item.SetText(text);
            foreach (DataFormat<byte[]> format in WindowsExclusionFormats)
            {
                item.Set(format, new byte[4]); // DWORD 0 = false / exclude
            }

            var data = new DataTransfer();
            data.Add(item);
            await clipboard.SetDataAsync(data).ConfigureAwait(true); // the clipboard owns (and disposes) it now
        }
        else
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }

        _last = clipboard;
        _lastText = text;
    }

    /// <summary>
    /// Clear the clipboard when it still holds what we last wrote. Pass <paramref name="expected"/>
    /// for a timed clear of a specific copy; omit it to clear whatever we last put there (lock/exit).
    /// Never throws: a missing clipboard must not block lock or shutdown.
    /// </summary>
    public static async Task ClearIfStillOursAsync(string? expected = null)
    {
        IClipboard? clipboard = _last;
        string? ours = expected ?? _lastText;
        if (clipboard is null || ours is null)
        {
            return;
        }

        try
        {
            if (string.Equals(await clipboard.TryGetTextAsync().ConfigureAwait(true), ours, StringComparison.Ordinal))
            {
                await clipboard.ClearAsync().ConfigureAwait(true);
            }
        }
        catch
        {
            // Unavailable after the window is gone, or a platform without GetText — leave it.
        }
        finally
        {
            if (expected is null || string.Equals(expected, _lastText, StringComparison.Ordinal))
            {
                _last = null;
                _lastText = null;
            }
        }
    }
}
