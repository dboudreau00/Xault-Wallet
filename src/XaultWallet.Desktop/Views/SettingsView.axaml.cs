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
        }
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
