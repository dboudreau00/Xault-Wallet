using Avalonia.Controls;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views;

public partial class AddWalletView : UserControl
{
    private AddWalletViewModel? _vm;

    public AddWalletView() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as AddWalletViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm = null;
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AddWalletViewModel.ShowVerifyOverlay) when _vm?.ShowVerifyOverlay == true:
                _ = Motion.FadeInAsync(VerifyScrim, 180);
                _ = Motion.RiseInAsync(VerifySheet, distance: 16, milliseconds: 300);
                break;
            case nameof(AddWalletViewModel.ShowConfirm) when _vm?.ShowConfirm == true:
                _ = Motion.FadeInAsync(ConfirmScrim, 180);
                _ = Motion.RiseInAsync(ConfirmSheet, distance: 16, milliseconds: 300);
                break;
        }
    }
}
