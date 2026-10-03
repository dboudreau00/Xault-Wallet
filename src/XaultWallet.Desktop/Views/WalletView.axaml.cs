using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views;

public partial class WalletView : UserControl
{
    public WalletView() => InitializeComponent();

    private async void CopyAddress_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WalletViewModel vm)
        {
            await CopyToClipboardAsync(vm.PrimaryAddress, "Address");
        }
    }

    private async void CopyLastTxId_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WalletViewModel vm)
        {
            await CopyToClipboardAsync(vm.LastTxId, "Transaction ID");
        }
    }

    private async void CopyLastTxKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WalletViewModel vm)
        {
            await CopyToClipboardAsync(vm.LastTxKey, "Transaction key");
        }
    }

    private async void CopyRowTxId_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HistoryRow row && !string.IsNullOrWhiteSpace(row.TxId))
        {
            await CopyToClipboardAsync(row.TxId, "Transaction ID");
        }
    }

    /// <summary>Save the history as CSV via the platform save dialog.</summary>
    private async void ExportHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WalletViewModel vm)
        {
            return;
        }

        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                return;
            }

            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export transaction history",
                SuggestedFileName = $"xaultwallet-history-{DateTime.Now:yyyyMMdd}.csv",
                DefaultExtension = "csv",
                FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
            });

            if (file is null)
            {
                return; // user cancelled
            }

            await using Stream stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.BuildHistoryCsv());
            vm.CopyNotice = "History exported.";
        }
        catch (Exception ex)
        {
            vm.CopyNotice = "Export failed: " + ex.Message;
        }
    }

    // Identifies the most recent copy; an auto-clear only fires if no newer copy happened since.
    private int _copySequence;

    /// <summary>How long copied wallet data (addresses, tx keys) stays on the clipboard.</summary>
    private static readonly TimeSpan ClipboardClearDelay = TimeSpan.FromSeconds(30);

    private async Task CopyToClipboardAsync(string? text, string what = "Value")
    {
        WalletViewModel? vm = DataContext as WalletViewModel;
        try
        {
            if (string.IsNullOrWhiteSpace(text)
                || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                // Never let a copy fail silently: the user may otherwise paste stale
                // clipboard content (e.g. an OLD address) somewhere irreversible.
                if (vm is not null)
                {
                    vm.CopyNotice = "Nothing was copied — the clipboard is unavailable.";
                }

                return;
            }

            await clipboard.SetTextAsync(text);
            if (vm is not null)
            {
                vm.CopyNotice = $"{what} copied — clipboard clears in {ClipboardClearDelay.TotalSeconds:0} s.";
            }

            // Auto-clear: wallet data shouldn't linger for whatever the user pastes next week.
            // Only clear if (a) no newer copy was made from the app and (b) the clipboard still
            // holds exactly what we put there — never stomp something the user copied elsewhere.
            // Uses the clipboard captured at copy time: after Lock this view is detached and
            // re-resolving TopLevel would return null, skipping the clear exactly when the
            // copied wallet data most needs to go away. The window (and its clipboard) outlive us.
            int seq = ++_copySequence;
            await Task.Delay(ClipboardClearDelay);
            if (seq == _copySequence
                && string.Equals(await clipboard.GetTextAsync(), text, StringComparison.Ordinal))
            {
                await clipboard.ClearAsync();
                if (vm is not null && seq == _copySequence)
                {
                    vm.CopyNotice = string.Empty;
                }
            }
        }
        catch
        {
            // Clipboard can be unavailable on some platforms/headless; tell the user rather
            // than pretend the copy happened.
            if (vm is not null)
            {
                vm.CopyNotice = "Copy failed — the clipboard is unavailable.";
            }
        }
    }
}
