using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views;

public partial class CreateWalletView : UserControl
{
    private CreateWalletViewModel? _vm;

    public CreateWalletView() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as CreateWalletViewModel;
        if (_vm is not null)
        {
            // Provide the VM a way to invoke the platform save dialog without a hard MVVM violation.
            _vm.SaveBackupHandler = SaveBackupAsync;
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
            case nameof(CreateWalletViewModel.RealSeedGenerated) when _vm?.RealSeedGenerated == true:
                _ = Motion.RiseInAsync(SeedPanel, distance: 10, milliseconds: 360); // the 25 words arrive
                break;
            case nameof(CreateWalletViewModel.ShowVerifyOverlay) when _vm?.ShowVerifyOverlay == true:
                _ = Motion.FadeInAsync(VerifyScrim, 180);
                _ = Motion.RiseInAsync(VerifySheet, distance: 16, milliseconds: 300);
                break;
            case nameof(CreateWalletViewModel.ShowAddressConfirm) when _vm?.ShowAddressConfirm == true:
                _ = Motion.FadeInAsync(AddressScrim, 180);
                _ = Motion.RiseInAsync(AddressSheet, distance: 16, milliseconds: 300);
                break;
        }
    }

    private async Task<bool> SaveBackupAsync(string contents, string suggestedName)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return false;
        }

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } },
            },
        });

        if (file is null)
        {
            return false;
        }

        await PickedFile.WriteTextAsync(file, contents);
        return true;
    }
}
