using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>The Contacts tab: the profile's address book (shared by all its wallets, sealed in the
/// vault), edited here.</summary>
public sealed partial class WalletViewModel
{
    [ObservableProperty] private bool _contactEditorOpen;
    [ObservableProperty] private string _contactName = string.Empty;
    [ObservableProperty] private string _contactAddress = string.Empty;
    [ObservableProperty] private string _contactNote = string.Empty;
    [ObservableProperty] private string _contactNotice = string.Empty;
    [ObservableProperty] private string _contactEditorTitle = "New contact";
    private ContactRow? _editingContact;

    [RelayCommand]
    private void NewContact() => StartNewContact(string.Empty);

    private void StartNewContact(string address)
    {
        _editingContact = null;
        ContactEditorTitle = "New contact";
        ContactName = string.Empty;
        ContactAddress = address;
        ContactNote = string.Empty;
        ContactNotice = string.Empty;
        ContactEditorOpen = true;
    }

    [RelayCommand]
    private void EditContact(ContactRow? row)
    {
        if (row is null)
        {
            return;
        }

        _editingContact = row;
        ContactEditorTitle = "Edit contact";
        ContactName = row.Name;
        ContactAddress = row.Address;
        ContactNote = row.Note;
        ContactNotice = string.Empty;
        ContactEditorOpen = true;
    }

    [RelayCommand]
    private void CancelContact()
    {
        ContactEditorOpen = false;
        _editingContact = null;
        ContactNotice = string.Empty;
    }

    [RelayCommand]
    private async Task SaveContactAsync()
    {
        string address = ContactAddress.Trim();

        // Contacts serve every wallet of the profile, whatever its network: accept an address that is
        // valid on any network (Send still checks it against the paying wallet's network).
        string? problem = MoneroAddress.Problem(address, _secrets.Network);
        if (problem is not null && Enum.GetValues<MoneroNetwork>().Any(n => MoneroAddress.Problem(address, n) is null))
        {
            problem = null;
        }

        if (problem is not null)
        {
            ContactNotice = problem;
            return;
        }

        if (MoneroUri.IsUri(address))
        {
            ContactNotice = "Paste the address itself, not a payment link.";
            return;
        }

        if (await _profile.SaveContactAsync(_editingContact, ContactName, address, ContactNote) is { } error)
        {
            ContactNotice = error;
            return;
        }

        Toast(_editingContact is null ? $"{ContactName.Trim()} added to contacts." : "Contact saved.");
        ContactEditorOpen = false;
        _editingContact = null;
        OnPropertyChanged(nameof(SendRecipientName));
        RebuildHistoryRows(); // destinations show contact names
    }

    [RelayCommand]
    private async Task DeleteContactAsync(ContactRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (await _profile.DeleteContactAsync(row) is { } error)
        {
            Toast(error, ok: false);
            return;
        }

        if (ReferenceEquals(row, _editingContact))
        {
            CancelContact();
        }

        Toast($"{row.Name} removed from contacts.");
        OnPropertyChanged(nameof(SendRecipientName));
        RebuildHistoryRows();
    }

    [RelayCommand]
    private void PayContactRow(ContactRow? row)
    {
        if (row is not null)
        {
            PayContact(row);
        }
    }

    [RelayCommand]
    private void CopyContactAddress(ContactRow? row)
    {
        if (row is not null)
        {
            Copy(row.Address, "Address");
        }
    }
}
