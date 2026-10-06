using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>
/// One line of the History list, with everything the view needs precomputed as plain one-way
/// strings. (The old DataGrid bound amounts TwoWay through a one-way converter: every row raised a
/// ConvertBack binding error and DISPLAYED 0 XMR for every transaction.) Click a row to see its
/// details and edit its note.
/// </summary>
public sealed partial class HistoryRow : ObservableObject
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

    /// <param name="entry">The transfer as wallet-rpc reports it.</param>
    /// <param name="walletHeight">The wallet's scanned height (for confirmations).</param>
    /// <param name="hideAmounts">Mask amounts (shoulder-surfing mode).</param>
    /// <param name="note">The user's note for this transaction.</param>
    /// <param name="receivedAt">Incoming: what the receiving address is called ("Main address", a label…).</param>
    /// <param name="destinations">Outgoing: one line per recipient (contact name or short address, amount).</param>
    public HistoryRow(TransferEntry entry, ulong walletHeight, bool hideAmounts,
        string note = "", string receivedAt = "", IReadOnlyList<string>? destinations = null)
    {
        Entry = entry;
        bool unconfirmed = entry.Type is "pool" or "pending" || entry.Height == 0;
        Confirmations = unconfirmed || walletHeight < entry.Height ? 0 : walletHeight - entry.Height;

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
            : Confirmations < ConfirmationsToUnlock(entry)
                ? $"{when} · {Confirmations}/{ConfirmationsToUnlock(entry)} confirmations"
                : $"{when} · block {entry.Height.ToString("N0", CultureInfo.InvariantCulture)}";

        ShortTxId = entry.TxId.Length > 20 ? $"{entry.TxId[..8]}…{entry.TxId[^8..]}" : entry.TxId;
        ConfirmationsText = unconfirmed
            ? "Not in a block yet (in the mempool)"
            : $"{Confirmations.ToString("N0", CultureInfo.InvariantCulture)} confirmation{(Confirmations == 1 ? "" : "s")} · block {entry.Height.ToString("N0", CultureInfo.InvariantCulture)}";
        ReceivedAt = receivedAt;
        Destinations = destinations ?? Array.Empty<string>();
        FeeLine = entry.Fee > 0 && !IsIncoming ? (hideAmounts ? Masked : XmrAmount.Format(entry.Fee) + " XMR") : string.Empty;
        _note = note;
        _noteDraft = note;
    }

    public TransferEntry Entry { get; }

    public string TxId => Entry.TxId;

    public ulong Confirmations { get; }

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

    // ---- details (shown when the row is expanded) ----

    public string ConfirmationsText { get; }

    /// <summary>Incoming: the receiving address's name.</summary>
    public string ReceivedAt { get; }

    public bool HasReceivedAt => ReceivedAt.Length > 0;

    /// <summary>Outgoing: where it went (known to the sending wallet only).</summary>
    public IReadOnlyList<string> Destinations { get; }

    public bool HasDestinations => Destinations.Count > 0;

    public string FeeLine { get; }

    public bool HasFeeLine => FeeLine.Length > 0;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>The saved note (shown under the title).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note;

    /// <summary>The note being edited in the details.</summary>
    [ObservableProperty] private string _noteDraft;

    public bool HasNote => Note.Length > 0;

    /// <summary>Text the history search matches against.</summary>
    public bool Matches(string query) =>
        query.Length == 0
        || TxId.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Note.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || ReceivedAt.Contains(query, StringComparison.OrdinalIgnoreCase)
        || AmountText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Destinations.Any(d => d.Contains(query, StringComparison.OrdinalIgnoreCase));
}
