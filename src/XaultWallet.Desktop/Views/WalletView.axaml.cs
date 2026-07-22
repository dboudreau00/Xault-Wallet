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

    private async Task CopyToClipboardAsync(string? text)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text)
                && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch
        {
            // Clipboard can be unavailable on some platforms/headless; ignore.
        }
    }
}
