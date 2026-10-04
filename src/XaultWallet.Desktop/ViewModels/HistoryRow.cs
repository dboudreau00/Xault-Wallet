using System.Globalization;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>
/// One line of the History list, with everything the view needs precomputed as plain one-way
/// strings. (The old DataGrid bound amounts TwoWay through a one-way converter: every row raised a
/// ConvertBack binding error and DISPLAYED 0 XMR for every transaction.)
/// </summary>
public sealed class HistoryRow
{
    /// <summary>Incoming outputs become spendable after this many confirmations.</summary>
    public const ulong UnlockConfirmations = 10;

    /// <summary>Mined coins (solo or P2Pool payouts arrive as coinbase outputs, which wallet-rpc
    /// reports with type "block") unlock only after 60 blocks: CRYPTONOTE_MINED_MONEY_UNLOCK_WINDOW.</summary>
    public const ulong MinedUnlockConfirmations = 60;

    /// <summary>Confirmations until this entry's outputs are spendable.</summary>
    public static ulong ConfirmationsToUnlock(TransferEntry entry) =>
        entry.Type == "block" ? MinedUnlockConfirmations : UnlockConfirmations;

    private const string Masked = "●●●●●";

    public HistoryRow(TransferEntry entry, ulong walletHeight, bool hideAmounts)
    {
        Entry = entry;
        bool unconfirmed = entry.Type is "pool" or "pending" || entry.Height == 0;
        ulong confirmations = unconfirmed || walletHeight < entry.Height ? 0 : walletHeight - entry.Height;

        // "block" = a mining reward: money IN. (It used to fall through to the outgoing style, so every
        // P2Pool payout read as "−35.1 XMR" under the raw title "block".)
        IsIncoming = entry.Type is "in" or "pool" or "block";
        IsPending = entry.Type is "pool" or "pending";
        IsFailed = entry.Type == "failed";
        Title = entry.Type switch
        {
            "in" => "Received",
            "out" => "Sent",
            "pool" => "Incoming",
            "pending" => "Sending",
            "failed" => "Failed",
            "block" => "Mining reward",
            _ => entry.Type,
        };

        string sign = IsIncoming ? "+" : "−";
        AmountText = hideAmounts ? Masked : $"{sign}{XmrAmount.Format(entry.Amount)} XMR";
        FeeText = !IsIncoming && entry.Fee > 0 && !hideAmounts ? $"fee {XmrAmount.Format(entry.Fee)}" : string.Empty;

        string when = entry.Timestamp == 0
            ? "just now"
            : DateTimeOffset.FromUnixTimeSeconds((long)entry.Timestamp).ToLocalTime().ToString("MMM d, yyyy · HH:mm", CultureInfo.InvariantCulture);
        Subtitle = unconfirmed
            ? $"{when} · waiting for a block"
            : confirmations < ConfirmationsToUnlock(entry)
                ? $"{when} · {confirmations}/{ConfirmationsToUnlock(entry)} confirmations"
                : $"{when} · block {entry.Height.ToString("N0", CultureInfo.InvariantCulture)}";

        ShortTxId = entry.TxId.Length > 20 ? $"{entry.TxId[..8]}…{entry.TxId[^8..]}" : entry.TxId;
    }

    public TransferEntry Entry { get; }

    public string TxId => Entry.TxId;

    public bool IsIncoming { get; }

    public bool IsOutgoing => !IsIncoming && !IsFailed;

    public bool IsPending { get; }

    public bool IsFailed { get; }

    public bool IsSettled => !IsPending && !IsFailed;

    public string Title { get; }

    public string Subtitle { get; }

    public string AmountText { get; }

    public string FeeText { get; }

    public bool HasFee => FeeText.Length > 0;

    /// <summary>Short tx id, plus the fee for outgoing transactions.</summary>
    public string DetailText => HasFee ? $"{ShortTxId}  ·  {FeeText}" : ShortTxId;

    public string ShortTxId { get; }
}
