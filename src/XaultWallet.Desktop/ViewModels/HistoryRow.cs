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

    private const string Masked = "●●●●●";

    public HistoryRow(TransferEntry entry, ulong walletHeight, bool hideAmounts)
    {
        Entry = entry;
        bool unconfirmed = entry.Type is "pool" or "pending" || entry.Height == 0;
        ulong confirmations = unconfirmed || walletHeight < entry.Height ? 0 : walletHeight - entry.Height;

        IsIncoming = entry.Type is "in" or "pool";
        IsPending = entry.Type is "pool" or "pending";
        IsFailed = entry.Type == "failed";
        Title = entry.Type switch
        {
            "in" => "Received",
            "out" => "Sent",
            "pool" => "Incoming",
            "pending" => "Sending",
            "failed" => "Failed",
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
            : confirmations < UnlockConfirmations
                ? $"{when} · {confirmations}/{UnlockConfirmations} confirmations"
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
