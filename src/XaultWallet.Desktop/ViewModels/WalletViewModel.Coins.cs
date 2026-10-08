using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>One unspent output ("coin") on the Coins tab.</summary>
public sealed partial class CoinRow : ObservableObject
{
    public CoinRow(OwnedOutput output, string amountText, string receivedAt, string age, bool canFreeze)
    {
        Output = output;
        AmountText = amountText;
        ReceivedAt = receivedAt;
        Age = age;
        CanFreeze = canFreeze;
        _isFrozen = output.Frozen;
    }

    public OwnedOutput Output { get; }

    public string KeyImage => Output.KeyImage;

    public string TxId => Output.TxHash;

    /// <summary>"3fa9c1…e77d": enough to tell coins apart at a glance.</summary>
    public string ShortTxId => TxId.Length > 16 ? TxId[..8] + "…" + TxId[^6..] : TxId;

    public string AmountText { get; }

    /// <summary>"Received at Alice" (the subaddress label), the who-knows-about-this-coin hint.</summary>
    public string ReceivedAt { get; }

    public string Age { get; }

    public bool IsLocked => !Output.Unlocked;

    /// <summary>A coin can be frozen when the wallet can spend and knows its key image.</summary>
    public bool CanFreeze { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreezeLabel))]
    private bool _isFrozen;

    public string FreezeLabel => IsFrozen ? "Unfreeze" : "Freeze";
}

public sealed partial class WalletViewModel
{
    /// <summary>The selected account's unspent outputs, largest first.</summary>
    public ObservableCollection<CoinRow> Coins { get; } = new();

    private IReadOnlyList<OwnedOutput> _outputs = Array.Empty<OwnedOutput>();

    [ObservableProperty] private bool _hasCoins;

    [ObservableProperty] private string _coinsSummary = string.Empty;

    [ObservableProperty] private string _coinsNotice = string.Empty;

    /// <summary>What frozen coins hold (atomic units), shown beside the balance.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrozen))]
    [NotifyPropertyChangedFor(nameof(FrozenDisplay))]
    private ulong _frozenAtomic;

    public bool HasFrozen => FrozenAtomic > 0;

    public string FrozenDisplay => HideBalances ? $"Frozen {Masked}" : $"Frozen {XmrAmount.Format(FrozenAtomic)} XMR";

    /// <summary>How many of this wallet's frozen coins the running backend has frozen (all of them
    /// once the scan has found them).</summary>
    public int FrozenCount => _secrets.FrozenKeyImages.Count;

    /// <summary>When a send fails for want of money while coins are frozen, say that those were left
    /// out on purpose: otherwise the balance and the refusal seem to disagree.</summary>
    private string FrozenHint(Exception ex) =>
        HasFrozen && ex is MoneroRpcClient.MoneroRpcException rpc && rpc.Message.Contains("money", StringComparison.OrdinalIgnoreCase)
            ? " Frozen coins (Coins tab) are never spent: unfreeze one to use it."
            : string.Empty;

    /// <summary>" · 2 frozen coins left out" for the send summary, or nothing.</summary>
    private string FrozenNote()
    {
        int n = Coins.Count(c => c.IsFrozen);
        return n == 0 ? string.Empty : n == 1 ? " · 1 frozen coin left out" : $" · {n} frozen coins left out";
    }

    /// <summary>Re-freeze what the vault says is frozen, then list the coins.</summary>
    private async Task RefreshCoinsAsync(CancellationToken ct)
    {
        if (CanSpend && _secrets.FrozenKeyImages.Count > 0)
        {
            await _wallet.ApplyFrozenAsync(_secrets.FrozenKeyImages, ct);
        }

        uint account = AccountIndex;
        IReadOnlyList<OwnedOutput> outputs = await _wallet.GetCoinsAsync(account, ct);
        if (account == AccountIndex && !SameCoins(_outputs, outputs))
        {
            SetCoins(outputs);
        }
    }

    /// <summary>Freeze again everything frozen, right before a transaction is built: a coin the scan
    /// found since the last refresh must not slip into it.</summary>
    private async Task ApplyFrozenCoinsAsync(CancellationToken ct)
    {
        if (CanSpend && _secrets.FrozenKeyImages.Count > 0)
        {
            await _wallet.ApplyFrozenAsync(_secrets.FrozenKeyImages, ct);
        }
    }

    /// <summary>Mark a coin frozen in this wallet's vault record (UI previews and tests only: no
    /// backend, nothing saved).</summary>
    internal void FreezeForPreview(string keyImage)
    {
        if (!_secrets.FrozenKeyImages.Contains(keyImage))
        {
            _secrets.FrozenKeyImages.Add(keyImage);
        }
    }

    /// <summary>Replace the coin list (also used by UI previews).</summary>
    internal void SetCoins(IReadOnlyList<OwnedOutput> outputs)
    {
        _outputs = outputs;
        RebuildCoinRows();
    }

    private void RebuildCoinRows()
    {
        Coins.Clear();
        ulong frozen = 0, total = 0;
        foreach (OwnedOutput o in _outputs)
        {
            // The vault is the source of truth for "frozen": wallet-rpc's flag lags until re-applied.
            o.Frozen = o.Frozen || (o.KeyImage.Length > 0 && _secrets.FrozenKeyImages.Contains(o.KeyImage));
            total += o.Amount;
            if (o.Frozen)
            {
                frozen += o.Amount;
            }

            Coins.Add(new CoinRow(o,
                HideBalances ? Masked : XmrAmount.Format(o.Amount) + " XMR",
                CoinReceivedAt(o),
                CoinAge(o),
                CanSpend && o.KeyImage.Length > 0));
        }

        FrozenAtomic = frozen;
        HasCoins = Coins.Count > 0;
        int frozenCount = Coins.Count(c => c.IsFrozen);
        string amounts = HideBalances ? string.Empty
            : $" · {XmrAmount.Format(total)} XMR" + (frozen > 0 ? $", {XmrAmount.Format(frozen)} of it frozen" : string.Empty);
        CoinsSummary = Coins.Count == 0 ? string.Empty
            : $"{Coins.Count} {(Coins.Count == 1 ? "coin" : "coins")}" + (frozenCount > 0 ? $" · {frozenCount} frozen" : string.Empty) + amounts;
    }

    private string CoinReceivedAt(OwnedOutput o)
    {
        uint a = o.SubaddrIndex.Major;
        uint i = o.SubaddrIndex.Minor;
        string label = LabelOf(a, i);
        return label.Length > 0 ? $"Received at {label}" : (a, i) switch
        {
            (0, 0) => "Received at the main address",
            (_, 0) => "Received at the account's address",
            _ => $"Received at subaddress #{i.ToString(CultureInfo.InvariantCulture)}",
        };
    }

    private string CoinAge(OwnedOutput o)
    {
        if (o.BlockHeight == 0 || Height <= o.BlockHeight)
        {
            return "in the pool";
        }

        ulong confirmations = Height - o.BlockHeight;
        string conf = confirmations.ToString("N0", CultureInfo.InvariantCulture) + (confirmations == 1 ? " confirmation" : " confirmations");
        return conf; // "maturing" is its own badge
    }

    private static bool SameCoins(IReadOnlyList<OwnedOutput> shown, IReadOnlyList<OwnedOutput> fresh) =>
        shown.Count == fresh.Count && shown.Zip(fresh).All(p =>
            p.First.KeyImage == p.Second.KeyImage && p.First.TxHash == p.Second.TxHash && p.First.Amount == p.Second.Amount
            && p.First.Frozen == p.Second.Frozen && p.First.Unlocked == p.Second.Unlocked && p.First.BlockHeight == p.Second.BlockHeight);

    /// <summary>Freeze or unfreeze a coin: in the running backend at once, and in the vault so it
    /// stays that way after a lock. Either both happen or neither.</summary>
    [RelayCommand]
    private async Task ToggleFreezeAsync(CoinRow? row)
    {
        if (row is null || !row.CanFreeze || !IsReady)
        {
            return;
        }

        bool freeze = !row.IsFrozen;
        string keyImage = row.KeyImage;
        CoinsNotice = string.Empty;
        try
        {
            await _wallet.SetFrozenAsync(keyImage, freeze, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            CoinsNotice = (freeze ? "Couldn't freeze that coin: " : "Couldn't unfreeze that coin: ") + Friendly(ex);
            return;
        }

        bool wasListed = _secrets.FrozenKeyImages.Contains(keyImage);
        if (freeze && !wasListed)
        {
            _secrets.FrozenKeyImages.Add(keyImage);
        }
        else if (!freeze)
        {
            _secrets.FrozenKeyImages.Remove(keyImage);
        }

        if (await _profile.SaveAsync() is { } error)
        {
            // Put both back: a coin frozen here but not in the vault would thaw at the next lock.
            if (freeze && !wasListed)
            {
                _secrets.FrozenKeyImages.Remove(keyImage);
            }
            else if (!freeze && wasListed)
            {
                _secrets.FrozenKeyImages.Add(keyImage);
            }

            try { await _wallet.SetFrozenAsync(keyImage, !freeze, _cts.Token); } catch (Exception ex) { Log.WarnOnce("coin-revert", "Coin freeze revert failed: " + ex.GetType().Name); }
            CoinsNotice = error;
            return;
        }

        row.IsFrozen = freeze;
        row.Output.Frozen = freeze;
        RebuildCoinRows();
        OnPropertyChanged(nameof(FrozenCount));
        Toast(freeze ? "Coin frozen: it won't be spent until you unfreeze it." : "Coin unfrozen.");
        _ = SoftRefreshAsync(); // the spendable balance changes with it
    }

    [RelayCommand]
    private void CopyCoinTxId(CoinRow? row)
    {
        if (row is not null)
        {
            Copy(row.TxId, "Transaction ID");
        }
    }
}
