using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views;

public partial class WalletView : UserControl
{
    public WalletView() => InitializeComponent();

    private async void CopyAddress_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WalletViewModel vm)
        {
            await CopyToClipboardAsync(vm.PrimaryAddress);
        }
    }

    private async void CopyLastTxId_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WalletViewModel vm)
        {
            await CopyToClipboardAsync(vm.LastTxId);
        }
    }

    private async void CopyLastTxKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WalletViewModel vm)
        {
            await CopyToClipboardAsync(vm.LastTxKey);
        }
    }

    // Identifies the most recent copy; an auto-clear only fires if no newer copy happened since.
    private int _copySequence;

    /// <summary>How long copied wallet data (addresses, tx keys) stays on the clipboard.</summary>
    private static readonly TimeSpan ClipboardClearDelay = TimeSpan.FromSeconds(30);

    private async Task CopyToClipboardAsync(string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)
                || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                return;
            }

            await clipboard.SetTextAsync(text);

            // Auto-clear: wallet data shouldn't linger for whatever the user pastes next week.
            // Only clear if (a) no newer copy was made from the app and (b) the clipboard still
            // holds exactly what we put there — never stomp something the user copied elsewhere.
            // Uses the clipboard captured at copy time: after Lock this view is detached and
            // re-resolving TopLevel would return null, skipping the clear exactly when the
            // copied wallet data most needs to go away. The window (and its clipboard) outlive us.
            int seq = ++_copySequence;
            await Task.Delay(ClipboardClearDelay);
            if (seq == _copySequence
                && string.Equals(await clipboard.GetTextAsync(), text, StringComparison.Ordinal))
            {
                await clipboard.ClearAsync();
            }
        }
        catch
        {
            // Clipboard can be unavailable on some platforms/headless; ignore.
        }
    }
}
