using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Views;

public partial class WalletView : UserControl
{
    private WalletViewModel? _vm;
    private bool _balanceShown;
    private int _toastSequence;
    private System.Threading.CancellationTokenSource? _toastMotion;

    public WalletView()
    {
        InitializeComponent();

        // A tab's content rises into place when it's chosen. SelectionChanged bubbles up from every
        // selector inside the tabs (the priority box...), hence the source check.
        Tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Tabs) && Tabs.SelectedContent is Visual page)
            {
                _ = Motion.RiseInAsync(page, distance: 8, milliseconds: 240);
            }
        };

        // Choosing a wallet starts it (a monero-wallet-rpc and a restore). A closed combo box steps
        // its selection on Up/Down and on the mouse wheel, which would start every wallet on the
        // way: here those keys open the list instead, and the choice is made in it (Enter or a click).
        Switcher.AddHandler(KeyDownEvent, OnSwitcherKeyDown, RoutingStrategies.Tunnel);
        Switcher.AddHandler(PointerWheelChangedEvent, OnSwitcherWheel, RoutingStrategies.Tunnel);
    }

    private void OnSwitcherKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (!Switcher.IsDropDownOpen)
        {
            Switcher.IsDropDownOpen = true;
            e.Handled = true;
            // The combo box tries this itself on opening, before its list is laid out (then nothing
            // gets focus, and the arrow keys do nothing in the list): again once it is.
            Dispatcher.UIThread.Post(FocusShownWallet, DispatcherPriority.Loaded);
        }
        else if (Switcher.IsFocused)
        {
            FocusShownWallet(); // open, but focus never went into the list
            e.Handled = true;
        }
    }

    /// <summary>Put keyboard focus on the shown wallet's entry in the open list: from there the arrow
    /// keys move through the list, and Enter chooses.</summary>
    private void FocusShownWallet()
    {
        if (Switcher.IsDropDownOpen && Switcher.ContainerFromIndex(Math.Max(0, Switcher.SelectedIndex)) is { } entry)
        {
            entry.Focus(NavigationMethod.Directional);
        }
    }

    private void OnSwitcherWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!Switcher.IsDropDownOpen && Switcher.IsFocused)
        {
            e.Handled = true;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unsubscribe();
        _vm = DataContext as WalletViewModel;
        _balanceShown = false;
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe();
        _vm = null;
        // Lock replaces this view: clear any wallet data we still own on the clipboard.
        _ = SensitiveClipboard.ClearIfStillOursAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is null && DataContext is WalletViewModel vm)
        {
            _vm = vm;
            Subscribe();
        }
    }

    private void Subscribe()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.CopyRequested += OnCopyRequested;
        _vm.ToastRequested += OnToastRequested;
        _vm.ExportHistoryRequested += OnExportHistoryRequested;
    }

    private void Unsubscribe()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged -= OnViewModelChanged;
        _vm.CopyRequested -= OnCopyRequested;
        _vm.ToastRequested -= OnToastRequested;
        _vm.ExportHistoryRequested -= OnExportHistoryRequested;
    }

    /// <summary>Motion that follows the wallet's state: what changed is where the eye goes.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(WalletViewModel.Balance):
                // Not the first figure after unlocking: only money arriving or leaving.
                if (_balanceShown)
                {
                    _ = Motion.PulseAsync(BalanceText);
                }

                _balanceShown = true;
                break;
            case nameof(WalletViewModel.ShowSendConfirm) when _vm.ShowSendConfirm:
                _ = Motion.FadeInAsync(ConfirmScrim, 180);
                _ = Motion.RiseInAsync(ConfirmSheet, distance: 16, milliseconds: 300);
                break;
            case nameof(WalletViewModel.ShowManage) when _vm.ShowManage:
                _ = Motion.FadeInAsync(ManageScrim, 180);
                _ = Motion.RiseInAsync(ManageSheet, distance: 16, milliseconds: 300);
                break;
        }
    }

    private void OnToastRequested(string text, bool ok) => ShowToast(text, ok);

    private void OnCopyRequested(string text, string what) => _ = CopyToClipboardAsync(text, what);

    private void OnExportHistoryRequested() => _ = ExportHistoryAsync();

    /// <summary>Brief feedback at the bottom of the window, on whichever tab is open.</summary>
    private async void ShowToast(string text, bool ok = true)
    {
        int sequence = ++_toastSequence;
        _toastMotion?.Cancel(); // e.g. the previous toast's fade-out: never two opacity animations at once
        var motion = _toastMotion = new System.Threading.CancellationTokenSource();
        ToastText.Text = text;
        ToastOk.IsVisible = ok;
        ToastProblem.IsVisible = !ok;
        Toast.IsVisible = true;
        await Motion.RiseInAsync(Toast, distance: 10, milliseconds: 220, motion.Token);
        await Task.Delay(TimeSpan.FromSeconds(ok ? 3.5 : 6));
        if (sequence != _toastSequence)
        {
            return; // a newer message took over
        }

        await Motion.FadeOutAsync(Toast, 240, motion.Token);
        if (sequence == _toastSequence)
        {
            Toast.IsVisible = false;
        }
    }

    /// <summary>Save the history as CSV via the platform save dialog.</summary>
    private async Task ExportHistoryAsync()
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

            await PickedFile.WriteTextAsync(file, vm.BuildHistoryCsv());
            ShowToast("History exported.");
        }
        catch (Exception ex)
        {
            ShowToast("Export failed: " + ex.Message, ok: false);
        }
    }

    // Identifies the most recent copy; an auto-clear only fires if no newer copy happened since.
    private int _copySequence;

    /// <summary>How long copied wallet data (addresses, tx keys) stays on the clipboard.</summary>
    private static readonly TimeSpan ClipboardClearDelay = TimeSpan.FromSeconds(30);

    private async Task CopyToClipboardAsync(string? text, string what = "Value")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)
                || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                // Never let a copy fail silently: the user may otherwise paste stale
                // clipboard content (e.g. an OLD address) somewhere irreversible.
                ShowToast("Nothing was copied — the clipboard is unavailable.", ok: false);
                return;
            }

            await SensitiveClipboard.SetAsync(clipboard, text);
            ShowToast($"{what} copied — the clipboard clears in {ClipboardClearDelay.TotalSeconds:0} s.");

            // Auto-clear: wallet data shouldn't linger for whatever the user pastes next week.
            // Only clear if (a) no newer copy was made from the app and (b) the clipboard still
            // holds exactly what we put there — never stomp something the user copied elsewhere.
            // Uses the clipboard captured at copy time: after Lock this view is detached and
            // re-resolving TopLevel would return null, skipping the clear exactly when the
            // copied wallet data most needs to go away. The window (and its clipboard) outlive us.
            int seq = ++_copySequence;
            await Task.Delay(ClipboardClearDelay);
            if (seq == _copySequence)
            {
                await SensitiveClipboard.ClearIfStillOursAsync(text);
            }
        }
        catch
        {
            // Clipboard can be unavailable on some platforms/headless; tell the user rather
            // than pretend the copy happened.
            ShowToast("Copy failed — the clipboard is unavailable.", ok: false);
        }
    }
}
