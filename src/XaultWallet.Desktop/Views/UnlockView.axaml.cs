using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views;

public partial class UnlockView : UserControl
{
    private UnlockViewModel? _vm;

    public UnlockView() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as UnlockViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm = null;
        }
    }

    // A wrong password shakes the card, the way a door handle says "locked". (Only failures
    // move: a duress unlock and a real one look and behave identically.)
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UnlockViewModel.Error) && !string.IsNullOrEmpty(_vm?.Error))
        {
            _ = Motion.ShakeAsync(LoginCard);
        }
    }
}
