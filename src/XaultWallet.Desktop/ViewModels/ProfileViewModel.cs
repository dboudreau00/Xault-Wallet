using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>A wallet as the switcher lists it.</summary>
public sealed partial class WalletChoice : ObservableObject
{
    private WalletSecrets _wallet;
    private bool _onLocalTestChain;

    public WalletChoice(WalletSecrets wallet)
    {
        Id = wallet.Id;
        _wallet = wallet;
        Refresh(wallet);
    }

    public string Id { get; }

    [ObservableProperty] private string _name = string.Empty;

    /// <summary>"Mainnet", "Stagenet · watch-only", "Regtest"…</summary>
    [ObservableProperty] private string _subtitle = string.Empty;

    public void Refresh(WalletSecrets wallet)
    {
        _wallet = wallet;
        Name = wallet.Name;
        UpdateSubtitle();
    }

    /// <summary>The wallet turned out to sync from a private test chain on this computer: say so,
    /// as its badge does, rather than "Mainnet" (regtest uses mainnet-format addresses).</summary>
    internal void SetLocalTestChain(bool value)
    {
        _onLocalTestChain = value;
        UpdateSubtitle();
    }

    private void UpdateSubtitle()
    {
        string kind = _wallet.Kind switch
        {
            WalletKind.ViewOnly => " · watch-only",
            WalletKind.Keys => " · from keys",
            _ => string.Empty,
        };
        Subtitle = (_onLocalTestChain ? "Regtest" : _wallet.Network.ToString()) + kind;
    }

    public override string ToString() => Name;
}

/// <summary>A saved recipient, as the Contacts tab and the Send picker show it.</summary>
public sealed partial class ContactRow : ObservableObject
{
    public ContactRow(Contact contact) => Model = contact;

    public Contact Model { get; }

    public string Id => Model.Id;

    public string Name => Model.Name;

    public string Address => Model.Address;

    public string Note => Model.Note;

    public string ShortAddress => Model.Address.Length > 20 ? Model.Address[..8] + "…" + Model.Address[^8..] : Model.Address;

    public bool HasNote => Model.Note.Length > 0;

    internal void Changed()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(ShortAddress));
        OnPropertyChanged(nameof(HasNote));
    }

    public override string ToString() => Name;
}

/// <summary>
/// Everything one password opened: an open <see cref="VaultSession"/>, its wallets and contacts.
/// Each wallet the user opens gets its own <see cref="WalletViewModel"/> and backend, kept running
/// until the vault is locked, so switching back is instant (only the wallet shown at unlock starts
/// right away). Changes are written back through the session. The inactivity lock lives here: it
/// locks the whole vault, whichever wallet is showing.
///
/// Nothing in here knows (or could know) whether this is the real profile or the decoy.
/// </summary>
public sealed partial class ProfileViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly VaultSession? _session;
    private readonly Dictionary<string, WalletViewModel> _open = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task? _lockLoop;
    private bool _disposed;
    private bool _switching;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public ProfileViewModel(VaultSession session)
        : this(session.Profile, session)
    {
        UpgradeNotice = session.UpgradedFromLegacyFormat ? LegacyUpgradeNotice : string.Empty;
        _lockLoop = AutoLockLoopAsync(_cts.Token);
    }

    private ProfileViewModel(WalletProfile profile, VaultSession? session)
    {
        _session = session;
        Profile = profile;
        foreach (WalletSecrets w in profile.Wallets)
        {
            Wallets.Add(new WalletChoice(w));
        }

        foreach (Contact c in profile.Contacts.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Contacts.Add(new ContactRow(c));
        }

        HasContacts = Contacts.Count > 0;
    }

    /// <summary>A profile with no vault behind it — for UI snapshots and tests only (saves do nothing).</summary>
    internal static ProfileViewModel ForPreview(WalletProfile profile) => new(profile, session: null);

    public WalletProfile Profile { get; }

    /// <summary>The wallets, for the switcher.</summary>
    public ObservableCollection<WalletChoice> Wallets { get; } = new();

    public ObservableCollection<ContactRow> Contacts { get; } = new();

    [ObservableProperty] private bool _hasContacts;

    /// <summary>The wallet on screen.</summary>
    [ObservableProperty] private WalletViewModel? _active;

    /// <summary>Bound to the switcher; choosing one opens it.</summary>
    [ObservableProperty] private WalletChoice? _selectedWallet;

    public bool HasSeveralWallets => Wallets.Count > 1;

    partial void OnSelectedWalletChanged(WalletChoice? value)
    {
        if (value is not null && !_switching)
        {
            Switch(value.Id);
        }
    }

    /// <summary>Raised when another wallet comes on screen.</summary>
    public event Action<WalletViewModel>? ActiveChanged;

    /// <summary>Raised once the vault is locked (session closed, backends stopped).</summary>
    public event Action? Locked;

    public event Action? SettingsRequested;

    /// <summary>Raised when the user asks to add a wallet; the shell shows that screen.</summary>
    public event Action? AddWalletRequested;

    internal void RequestSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private void AddWallet() => AddWalletRequested?.Invoke();

    // ------------------------------------------------------------------ the 0.1 upgrade notice

    /// <summary>Shown once, after a vault from 0.1 is opened and re-sealed in the current format.</summary>
    internal const string LegacyUpgradeNotice =
        "This vault was created by XaultWallet 0.1 and has been upgraded. Only the part this password " +
        "opens was converted; any other password converts when it is next used. See “Upgrading " +
        "from 0.1” in SECURITY.md (included in the download) before you use another password with this vault.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpgradeNotice))]
    private string _upgradeNotice = string.Empty;

    public bool HasUpgradeNotice => UpgradeNotice.Length > 0;

    [RelayCommand]
    private void DismissUpgradeNotice() => UpgradeNotice = string.Empty;

    // ------------------------------------------------------------------ wallets

    /// <summary>Open the wallet that was in use last (or the first).</summary>
    public WalletViewModel Start()
    {
        WalletSecrets first = Profile.ActiveWallet ?? throw new InvalidOperationException("The vault holds no wallet.");
        Switch(first.Id);
        return Active!;
    }

    /// <summary>Bring a wallet on screen, starting its backend the first time.</summary>
    public void Switch(string id)
    {
        if (_disposed || Active?.WalletId == id)
        {
            return;
        }

        WalletSecrets? wallet = Profile.Wallets.FirstOrDefault(w => w.Id == id);
        if (wallet is null)
        {
            return;
        }

        if (!_open.TryGetValue(id, out WalletViewModel? vm))
        {
            vm = _session is null ? WalletViewModel.ForPreview(this, wallet) : new WalletViewModel(this, wallet);
            _open[id] = vm;
        }

        WalletViewModel? previous = Active;
        Active = vm;
        previous?.OnBackground();
        vm.OnForeground();

        _switching = true;
        SelectedWallet = Wallets.FirstOrDefault(c => c.Id == id);
        _switching = false;

        if (Profile.ActiveWalletId != id)
        {
            Profile.ActiveWalletId = id; // opens first next time
            _ = SaveAsync();
        }

        ActiveChanged?.Invoke(vm);
    }

    /// <summary>Add a wallet to this profile, save it, and switch to it. Returns an error message
    /// (and adds nothing) when the vault couldn't be written.</summary>
    public async Task<string?> AddAsync(WalletSecrets wallet)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        Profile.Wallets.Add(wallet);
        string? error = await SaveAsync();
        if (error is not null)
        {
            Profile.Wallets.Remove(wallet);
            return error;
        }

        Wallets.Add(new WalletChoice(wallet));
        OnPropertyChanged(nameof(HasSeveralWallets));
        Switch(wallet.Id);
        return null;
    }

    /// <summary>Rename a wallet. Returns an error message, or null.</summary>
    public async Task<string?> RenameAsync(string id, string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return $"A wallet name is 1 to {MaxNameLength} characters.";
        }

        WalletSecrets? wallet = Profile.Wallets.FirstOrDefault(w => w.Id == id);
        if (wallet is null)
        {
            return "That wallet is no longer in this vault.";
        }

        string before = wallet.Name;
        wallet.Name = trimmed;
        string? error = await SaveAsync();
        if (error is not null)
        {
            wallet.Name = before;
            return error;
        }

        Wallets.FirstOrDefault(c => c.Id == id)?.Refresh(wallet);
        _open.GetValueOrDefault(id)?.OnRenamed();
        return null;
    }

    public const int MaxNameLength = 40;

    /// <summary>A wallet found out where it syncs from: its switcher entry says so.</summary>
    internal void NoteLocalTestChain(string walletId, bool onLocalTestChain) =>
        Wallets.FirstOrDefault(c => c.Id == walletId)?.SetLocalTestChain(onLocalTestChain);

    /// <summary>The name of the wallet in this profile whose main address this is, if any. Key-restored
    /// wallets store their address; a seed wallet's is known once it has been opened.</summary>
    internal string? NameOfWalletWithAddress(string address)
    {
        foreach (WalletSecrets w in Profile.Wallets)
        {
            bool same = string.Equals(w.Address, address, StringComparison.Ordinal)
                || (_open.TryGetValue(w.Id, out WalletViewModel? vm) && string.Equals(vm.PrimaryAddress, address, StringComparison.Ordinal));
            if (same)
            {
                return w.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Remove a wallet from the vault after checking the vault password (the seed is gone for good
    /// unless the user has a backup). The last wallet can't be removed. Returns an error message, or null.
    /// </summary>
    public async Task<string?> RemoveAsync(string id, char[] password)
    {
        try
        {
            if (Profile.Wallets.Count <= 1)
            {
                return "This is the only wallet in the vault, so it can't be removed.";
            }

            if (!await CheckPasswordAsync(password))
            {
                return "Incorrect password.";
            }

            WalletSecrets? wallet = Profile.Wallets.FirstOrDefault(w => w.Id == id);
            if (wallet is null)
            {
                return null;
            }

            int index = Profile.Wallets.IndexOf(wallet);
            Profile.Wallets.Remove(wallet);
            string? error = await SaveAsync();
            if (error is not null)
            {
                Profile.Wallets.Insert(index, wallet);
                return error;
            }

            if (Active?.WalletId == id)
            {
                Switch(Profile.Wallets[0].Id);
            }

            if (_open.Remove(id, out WalletViewModel? vm))
            {
                await vm.DisposeAsync(); // stops its backend and shreds its session folder
            }

            WalletChoice? choice = Wallets.FirstOrDefault(c => c.Id == id);
            if (choice is not null)
            {
                Wallets.Remove(choice);
            }

            OnPropertyChanged(nameof(HasSeveralWallets));
            return null;
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>True when <paramref name="password"/> is this profile's password. Runs Argon2 off the
    /// UI thread. Zeroes the password.</summary>
    public async Task<bool> CheckPasswordAsync(char[] password)
    {
        if (_session is null)
        {
            Array.Clear(password);
            return false;
        }

        return await Task.Run(() =>
        {
            using var pw = SecureBuffer.FromPassword(password);
            return _session.CheckPassword(pw);
        });
    }

    /// <summary>Change this profile's password (only this profile's: see VaultSession).</summary>
    public async Task<bool> ChangePasswordAsync(char[] current, char[] next)
    {
        if (_session is null)
        {
            Array.Clear(current);
            Array.Clear(next);
            return false;
        }

        return await Task.Run(() =>
        {
            using var cur = SecureBuffer.FromPassword(current);
            using var nxt = SecureBuffer.FromPassword(next);
            return _session.ChangePassword(cur, nxt);
        });
    }

    // ------------------------------------------------------------------ contacts

    /// <summary>Add or update a contact. Returns an error message, or null.</summary>
    public async Task<string?> SaveContactAsync(ContactRow? existing, string name, string address, string note)
    {
        string n = (name ?? string.Empty).Trim();
        string a = (address ?? string.Empty).Trim();
        string t = (note ?? string.Empty).Trim();
        if (n.Length is 0 or > 60)
        {
            return "Give the contact a name (up to 60 characters).";
        }

        if (t.Length > 200)
        {
            return "Keep the note under 200 characters.";
        }

        if (Contacts.Any(c => !ReferenceEquals(c, existing) && string.Equals(c.Address, a, StringComparison.Ordinal)))
        {
            return "That address is already saved as " + Contacts.First(c => string.Equals(c.Address, a, StringComparison.Ordinal)).Name + ".";
        }

        if (existing is null)
        {
            var contact = new Contact { Name = n, Address = a, Note = t };
            Profile.Contacts.Add(contact);
            string? error = await SaveAsync();
            if (error is not null)
            {
                Profile.Contacts.Remove(contact);
                return error;
            }

            InsertSorted(new ContactRow(contact));
        }
        else
        {
            (string oldName, string oldAddress, string oldNote) = (existing.Model.Name, existing.Model.Address, existing.Model.Note);
            existing.Model.Name = n;
            existing.Model.Address = a;
            existing.Model.Note = t;
            string? error = await SaveAsync();
            if (error is not null)
            {
                existing.Model.Name = oldName;
                existing.Model.Address = oldAddress;
                existing.Model.Note = oldNote;
                return error;
            }

            existing.Changed();
            Contacts.Remove(existing);
            InsertSorted(existing);
        }

        HasContacts = Contacts.Count > 0;
        return null;
    }

    public async Task<string?> DeleteContactAsync(ContactRow row)
    {
        int index = Profile.Contacts.IndexOf(row.Model);
        if (index < 0)
        {
            return null;
        }

        Profile.Contacts.RemoveAt(index);
        string? error = await SaveAsync();
        if (error is not null)
        {
            Profile.Contacts.Insert(index, row.Model);
            return error;
        }

        Contacts.Remove(row);
        HasContacts = Contacts.Count > 0;
        return null;
    }

    /// <summary>The saved name for an address, if any.</summary>
    public string? ContactNameFor(string? address) =>
        string.IsNullOrWhiteSpace(address) ? null
            : Contacts.FirstOrDefault(c => string.Equals(c.Address, address.Trim(), StringComparison.Ordinal))?.Name;

    private void InsertSorted(ContactRow row)
    {
        int i = 0;
        while (i < Contacts.Count && string.Compare(Contacts[i].Name, row.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)
        {
            i++;
        }

        Contacts.Insert(i, row);
    }

    // ------------------------------------------------------------------ saving

    /// <summary>Write the profile back to the vault, one write at a time: each serializes the
    /// profile on the UI thread (where it changes) when its turn comes, then encrypts and writes it in
    /// the background. Returns null on success, else a message to show.</summary>
    public async Task<string?> SaveAsync()
    {
        if (_session is null)
        {
            return null; // preview
        }

        await _saveGate.WaitAsync();
        try
        {
            await _session.SaveAsync();
            return null;
        }
        catch (VaultFullException ex)
        {
            return ex.Message;
        }
        catch (ObjectDisposedException)
        {
            return null; // locked before this save's turn came
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("Saving the vault failed", ex);
            return "Couldn't save to the vault: " + ex.Message;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    // ------------------------------------------------------------------ inactivity lock

    [ObservableProperty] private bool _lockImminent;
    [ObservableProperty] private string _lockCountdownText = string.Empty;

    /// <summary>Called from the window on any user input.</summary>
    public void NotifyActivity() => _lastActivityUtc = DateTime.UtcNow;

    /// <summary>The "Stay unlocked" button on the countdown strip.</summary>
    [RelayCommand]
    private void StayUnlocked()
    {
        NotifyActivity();
        LockImminent = false;
    }

    private async Task AutoLockLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Auto-lock after inactivity (0 = disabled), with a visible countdown for the last
                // 30 seconds so the wallet never just vanishes mid-read.
                int lockMinutes = AppServices.Instance.AutoLockMinutes;
                if (lockMinutes > 0)
                {
                    TimeSpan remaining = TimeSpan.FromMinutes(lockMinutes) - (DateTime.UtcNow - _lastActivityUtc);
                    if (remaining <= TimeSpan.Zero)
                    {
                        Log.Info("Auto-locking after inactivity.");
                        LockImminent = false;
                        // Fire-and-forget, then RETURN: LockAsync disposes this view model, which
                        // awaits this very loop — a task can never await itself finishing.
                        _ = LockAsync();
                        return;
                    }

                    LockImminent = remaining <= TimeSpan.FromSeconds(30);
                    if (LockImminent)
                    {
                        LockCountdownText = $"Locking in {Math.Max(1, (int)remaining.TotalSeconds)} s due to inactivity";
                    }
                }
                else
                {
                    LockImminent = false;
                }

                await Task.Delay(LockImminent ? 1000 : 3000, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // locked
        }
    }

    // ------------------------------------------------------------------ lock

    /// <summary>Lock: stop every wallet's backend (shredding its files), close the session (zeroing
    /// its key), and tell the shell.</summary>
    [RelayCommand]
    public async Task LockAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisposeAsync();
        Locked?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { await _cts.CancelAsync(); } catch (ObjectDisposedException) { }

        if (_lockLoop is not null)
        {
            try { await _lockLoop; } catch (OperationCanceledException) { }
        }

        // Stop all backends together: each kill waits for its process, no reason to queue them.
        WalletViewModel[] open = _open.Values.ToArray();
        _open.Clear();
        await Task.WhenAll(open.Select(vm => vm.DisposeAsync().AsTask()));

        // A save in flight finishes first: locking mid-write would drop the change it carries.
        await _saveGate.WaitAsync();
        try
        {
            _session?.Dispose();
        }
        finally
        {
            _saveGate.Release();
        }

        _cts.Dispose();
    }
}
