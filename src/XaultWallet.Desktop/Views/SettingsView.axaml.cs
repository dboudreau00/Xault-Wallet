using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsViewModel vm)
        {
            vm.BrowseHandler = BrowseForBinaryAsync;
            vm.BrowseTorHandler = BrowseForTorAsync;
            vm.ExportPickHandler = PickBackupDestinationAsync;
            vm.RestorePickHandler = PickBackupSourceAsync;
        }
    }

    private async Task<string?> PickBackupDestinationAsync(string suggestedName)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export encrypted vault backup",
            SuggestedFileName = suggestedName,
            DefaultExtension = "xv",
            FileTypeChoices = new[] { new FilePickerFileType("XaultWallet vault") { Patterns = new[] { "*.xv" } } },
        });

        return file?.Path.LocalPath;
    }

    private async Task<string?> PickBackupSourceAsync()
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore vault from backup",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("XaultWallet vault") { Patterns = new[] { "*.xv" } },
                FilePickerFileTypes.All,
            },
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private async Task<string?> BrowseForTorAsync()
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select tor",
            AllowMultiple = false,
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private async Task<string?> BrowseForBinaryAsync()
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select monero-wallet-rpc",
            AllowMultiple = false,
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private async void OpenLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(new Uri("https://dboudreau.dev"));
            }
        }
        catch
        {
            // Browser launch can be unavailable on some platforms; ignore.
        }
    }
}
