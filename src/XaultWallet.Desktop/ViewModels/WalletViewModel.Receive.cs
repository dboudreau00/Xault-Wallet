using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>One of the wallet's addresses (main address or a subaddress) in the Receive list.</summary>
public sealed partial class AddressRow : ObservableObject
{
    public AddressRow(uint account, uint index, string address, string label, bool used)
    {
        Account = account;
        Index = index;
        Address = address;
        _label = label;
        _labelDraft = label;
        _used = used;
    }

    public uint Account { get; }

    public uint Index { get; }

    public string Address { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _label;

    [ObservableProperty] private string _labelDraft;

    /// <summary>Has received a payment (so handing it to someone new links them to that payer).</summary>
    [ObservableProperty] private bool _used;

    [ObservableProperty] private bool _isEditing;

    /// <summary>It is the address the QR code shows.</summary>
    [ObservableProperty] private bool _isShown;

    public string Title => Label.Length > 0 ? Label : Index == 0 ? "Main address" : $"Subaddress #{Index}";

    public string ShortAddress => Address.Length > 24 ? Address[..10] + "…" + Address[^10..] : Address;
}

public sealed partial class WalletViewModel
{
    /// <summary>The wallet's main address (account 0, index 0).</summary>
    [ObservableProperty] private string _primaryAddress = string.Empty;

    /// <summary>The address the Receive tab shows and its QR code encodes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceiveUri))]
    [NotifyPropertyChangedFor(nameof(ReceiveTitle))]
    private string _receiveAddress = string.Empty;

    partial void OnReceiveAddressChanged(string value)
    {
        foreach (AddressRow row in Addresses)
        {
            row.IsShown = row.Address == value;
        }
    }

    /// <summary>What the shown address is called ("Main address", a label…).</summary>
    public string ReceiveTitle => Addresses.FirstOrDefault(a => a.Address == ReceiveAddress)?.Title ?? "Your address";

    /// <summary>Optional amount for a payment request (encoded in the QR and the link).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceiveUri))]
    [NotifyPropertyChangedFor(nameof(HasRequest))]
    private string _requestAmountText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceiveUri))]
    [NotifyPropertyChangedFor(nameof(HasRequest))]
    private string _requestDescription = string.Empty;

    [ObservableProperty] private string _requestAmountError = string.Empty;

    partial void OnRequestAmountTextChanged(string value)
    {
        RequestAmountError = value.Trim().Length == 0 || XmrAmount.TryParse(value, out decimal a, out _) && a > 0m
            ? string.Empty
            : "Enter an amount like 0.25 (or leave it empty).";
    }

    /// <summary>The requested amount, or null for "any amount".</summary>
    public decimal? RequestAmount =>
        XmrAmount.TryParse(RequestAmountText, out decimal a, out _) && a > 0m ? a : null;

    public bool HasRequest => RequestAmount is not null || RequestDescription.Trim().Length > 0;

    /// <summary>What the Receive QR encodes: a standard monero: link for the shown address, with the
    /// requested amount and description when set.</summary>
    public string ReceiveUri => string.IsNullOrEmpty(ReceiveAddress)
        ? string.Empty
        : MoneroUri.Build(new MoneroPaymentRequest(ReceiveAddress, RequestAmount, RequestDescription));

    [RelayCommand]
    private void ClearRequest()
    {
        RequestAmountText = string.Empty;
        RequestDescription = string.Empty;
    }

    /// <summary>Feedback for the Receive tab (new-subaddress errors) — kept OFF the Status line,
    /// which the sync tracker overwrites every few seconds.</summary>
    [ObservableProperty] private string _receiveNotice = string.Empty;

    /// <summary>Optional label for the next new subaddress ("Alice", "Shop sales").</summary>
    [ObservableProperty] private string _newAddressLabel = string.Empty;

    /// <summary>The selected account's addresses, main address first.</summary>
    public ObservableCollection<AddressRow> Addresses { get; } = new();

    public const int MaxLabelLength = 40;

    private string LabelOf(uint account, uint index) =>
        _secrets.Labels.GetValueOrDefault($"{account}/{index}") ?? string.Empty;

    /// <summary>Bring the address list in line with the wallet: new subaddresses appear, and an
    /// address that has received a payment is marked used.</summary>
    private async Task RefreshAddressesAsync(CancellationToken ct)
    {
        uint account = AccountIndex;
        IReadOnlyList<AddressInfo> addresses = await _wallet.GetAddressesAsync(account, ct);
        if (account != AccountIndex)
        {
            return;
        }

        foreach (AddressInfo a in addresses)
        {
            AddressRow? row = Addresses.FirstOrDefault(r => r.Index == a.AddressIndex);
            if (row is null)
            {
                Addresses.Add(new AddressRow(account, a.AddressIndex, a.Address, LabelOf(account, a.AddressIndex), a.Used)
                {
                    IsShown = a.Address == ReceiveAddress,
                });
            }
            else
            {
                row.Used = a.Used;
            }
        }

        if (ReceiveAddress.Length == 0 && Addresses.Count > 0)
        {
            ReceiveAddress = Addresses[0].Address;
        }

        OnPropertyChanged(nameof(ReceiveTitle));
    }

    /// <summary>A fresh subaddress for the next payer: unlinkable to the others, same wallet. The new
    /// count is saved to the vault, so after a lock the numbering continues where it stopped — the
    /// same subaddress is never handed out twice.</summary>
    [RelayCommand]
    private async Task NewAddressAsync()
    {
        if (!IsReady)
        {
            return;
        }

        string label = NewAddressLabel.Trim();
        if (label.Length > MaxLabelLength)
        {
            ReceiveNotice = $"Keep the label under {MaxLabelLength} characters.";
            return;
        }

        try
        {
            ReceiveNotice = string.Empty;
            uint account = AccountIndex;
            (uint index, string address) = await _wallet.NewSubaddressAsync(account, label, _cts.Token);
            _secrets.SubaddressCounts[account] = Math.Max(_secrets.SubaddressCounts.GetValueOrDefault(account), index + 1);
            if (label.Length > 0)
            {
                _secrets.Labels[$"{account}/{index}"] = label;
            }

            if (await _profile.SaveAsync() is { } error)
            {
                ReceiveNotice = "The new subaddress works, but it couldn't be recorded in the vault, so it may be handed out again after you lock: " + error;
            }

            NewAddressLabel = string.Empty;
            await RefreshAddressesAsync(_cts.Token);
            ReceiveAddress = address;
        }
        catch (OperationCanceledException)
        {
            // locked meanwhile
        }
        catch (Exception ex)
        {
            // NOT Status: the sync tracker overwrites Status every few seconds, so the
            // failure would vanish before the user saw it.
            ReceiveNotice = "Couldn't create a new address: " + Friendly(ex);
        }
    }

    /// <summary>Show this address (and its QR code).</summary>
    [RelayCommand]
    private void ShowAddress(AddressRow? row)
    {
        if (row is not null)
        {
            ReceiveAddress = row.Address;
        }
    }

    /// <summary>Copy an address from the list, or (no parameter) the one shown.</summary>
    [RelayCommand]
    private void CopyAddress(AddressRow? row) => Copy(row?.Address ?? ReceiveAddress, "Address");

    [RelayCommand]
    private void CopyPaymentLink() => Copy(ReceiveUri, "Payment link");

    [RelayCommand]
    private void EditLabel(AddressRow? row)
    {
        if (row is null)
        {
            return;
        }

        foreach (AddressRow other in Addresses)
        {
            other.IsEditing = false;
        }

        row.LabelDraft = row.Label;
        row.IsEditing = true;
    }

    [RelayCommand]
    private async Task SaveLabelAsync(AddressRow? row)
    {
        if (row is null)
        {
            return;
        }

        string label = row.LabelDraft.Trim();
        if (label.Length > MaxLabelLength)
        {
            ReceiveNotice = $"Keep the label under {MaxLabelLength} characters.";
            return;
        }

        string key = $"{row.Account}/{row.Index}";
        string? before = _secrets.Labels.GetValueOrDefault(key);
        if (label.Length == 0)
        {
            _secrets.Labels.Remove(key);
        }
        else
        {
            _secrets.Labels[key] = label;
        }

        if (await _profile.SaveAsync() is { } error)
        {
            if (before is null)
            {
                _secrets.Labels.Remove(key);
            }
            else
            {
                _secrets.Labels[key] = before;
            }

            ReceiveNotice = error;
            return;
        }

        row.Label = label;
        row.IsEditing = false;
        ReceiveNotice = string.Empty;
        OnPropertyChanged(nameof(ReceiveTitle));
        RebuildHistoryRows(); // "received at" names come from these labels
    }

    [RelayCommand]
    private void CancelLabel(AddressRow? row)
    {
        if (row is not null)
        {
            row.IsEditing = false;
        }

        ReceiveNotice = string.Empty;
    }
}
