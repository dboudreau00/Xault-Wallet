using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views;

public partial class CreateWalletView : UserControl
{
    public CreateWalletView() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is CreateWalletViewModel vm)
        {
            // Provide the VM a way to invoke the platform save dialog without a hard MVVM violation.
            vm.SaveBackupHandler = SaveBackupAsync;
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

        await using System.IO.Stream stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(contents);
        return true;
    }
}
