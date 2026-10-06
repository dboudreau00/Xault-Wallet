using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views.Wallet;

public partial class HistoryTab : UserControl
{
    public HistoryTab() => InitializeComponent();

    /// <summary>A click anywhere on a row opens its details (the chevron button does the same for
    /// the keyboard and screen readers). Clicks on the row's own buttons are theirs.</summary>
    private void Row_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if ((sender as Control)?.DataContext is HistoryRow row && DataContext is WalletViewModel vm)
        {
            vm.ToggleRowCommand.Execute(row);
        }
    }
}
