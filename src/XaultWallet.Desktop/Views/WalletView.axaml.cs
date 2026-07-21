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
        try
        {
            if (DataContext is WalletViewModel vm
                && !string.IsNullOrWhiteSpace(vm.PrimaryAddress)
                && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(vm.PrimaryAddress);
            }
        }
        catch
        {
            // Clipboard can be unavailable on some platforms/headless; ignore.
        }
    }
}
