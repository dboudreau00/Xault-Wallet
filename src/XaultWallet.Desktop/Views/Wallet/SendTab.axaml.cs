using System;
using System.ComponentModel;
using Avalonia.Controls;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views.Wallet;

public partial class SendTab : UserControl
{
    private WalletViewModel? _vm;

    public SendTab() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as WalletViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }
    }

    /// <summary>The outcome of a send pops in: it's where the eye should go.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.SendResult) && _vm is { SendResult.Length: > 0 })
        {
            _ = Motion.PopInAsync(SendOutcomeCard);
        }
    }
}
