using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

public sealed partial class WalletViewModel
{
    /// <summary>Display rows for the History list (rebuilt from <see cref="_entries"/>, filtered).</summary>
    public ObservableCollection<HistoryRow> History { get; } = new();

    /// <summary>The raw transfers as last fetched — the source of truth for rows and CSV export.</summary>
    private IReadOnlyList<TransferEntry> _entries = Array.Empty<TransferEntry>();

    /// <summary>Drives the History tab's empty state: there is no history at all.</summary>
    [ObservableProperty] private bool _hasHistory;

    /// <summary>History exists but the filter/search hides all of it.</summary>
    [ObservableProperty] private bool _noHistoryMatches;

    /// <summary>0 all, 1 incoming, 2 outgoing, 3 pending.</summary>
    [ObservableProperty] private int _historyFilter;

    [ObservableProperty] private string _historySearch = string.Empty;

    [ObservableProperty] private string _historyNotice = string.Empty;

    partial void OnHistoryFilterChanged(int value) => RebuildHistoryRows();

    partial void OnHistorySearchChanged(string value) => RebuildHistoryRows();

    /// <summary>Replace the transfer list (also used by UI previews).</summary>
    internal void SetHistory(IReadOnlyList<TransferEntry> entries)
    {
        _entries = entries;
        RebuildHistoryRows();
    }

    private void RebuildHistoryRows()
    {
        string? expanded = History.FirstOrDefault(r => r.IsExpanded)?.TxId;
        History.Clear();
        string query = HistorySearch.Trim();
        foreach (TransferEntry t in _entries)
        {
            HistoryRow row = BuildRow(t);
            bool shown = HistoryFilter switch
            {
                1 => row.IsIncoming,
                2 => row.IsOutgoing,
                3 => row.IsPending,
                _ => true,
            };
            if (shown && row.Matches(query))
            {
                row.IsExpanded = row.TxId == expanded;
                History.Add(row);
            }
        }

        HasHistory = _entries.Count > 0;
        NoHistoryMatches = HasHistory && History.Count == 0;
    }

    private HistoryRow BuildRow(TransferEntry t)
    {
        string note = _secrets.TxNotes.GetValueOrDefault(t.TxId) ?? string.Empty;
        string receivedAt = string.Empty;
        IReadOnlyList<string>? destinations = null;
        if (t.Type is "in" or "pool" or "block")
        {
            uint a = t.SubaddrIndex.Major;
            uint i = t.SubaddrIndex.Minor;
            string label = LabelOf(a, i);
            string name = label.Length > 0 ? label : i == 0 ? "the main address" : $"subaddress #{i}";
            string account = HasSeveralAccounts ? $" of {Accounts.FirstOrDefault(c => c.Index == a)?.Name ?? $"account #{a}"}" : string.Empty;
            receivedAt = $"Received at {name}{account}";
        }
        else if (t.Destinations is { Count: > 0 } dests)
        {
            destinations = dests.Select(d => $"{Who(d.Address)} · {(HideBalances ? Masked : XmrAmount.Format(d.Amount) + " XMR")}").ToList();
        }

        return new HistoryRow(t, Height, HideBalances, note, receivedAt, destinations);
    }

    private static bool SameHistory(IReadOnlyList<TransferEntry> shown, IReadOnlyList<TransferEntry> fresh)
    {
        if (shown.Count != fresh.Count)
        {
            return false;
        }

        for (int i = 0; i < fresh.Count; i++)
        {
            TransferEntry a = shown[i], b = fresh[i];
            if (a.TxId != b.TxId || a.Type != b.Type || a.Height != b.Height || a.Amount != b.Amount || a.Timestamp != b.Timestamp)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Open or close a row's details.</summary>
    [RelayCommand]
    private void ToggleRow(HistoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        bool open = !row.IsExpanded;
        foreach (HistoryRow other in History)
        {
            other.IsExpanded = false;
        }

        row.IsExpanded = open;
        row.NoteDraft = row.Note;
    }

    /// <summary>Save a transaction's note in the vault (the wallet file itself is shredded on lock).</summary>
    [RelayCommand]
    private async Task SaveNoteAsync(HistoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        string note = row.NoteDraft.Trim();
        if (note.Length > 500)
        {
            HistoryNotice = "Keep the note under 500 characters.";
            return;
        }

        string? before = _secrets.TxNotes.GetValueOrDefault(row.TxId);
        if (note.Length == 0)
        {
            _secrets.TxNotes.Remove(row.TxId);
        }
        else
        {
            _secrets.TxNotes[row.TxId] = note;
        }

        if (await _profile.SaveAsync() is { } error)
        {
            if (before is null)
            {
                _secrets.TxNotes.Remove(row.TxId);
            }
            else
            {
                _secrets.TxNotes[row.TxId] = before;
            }

            HistoryNotice = error;
            return;
        }

        HistoryNotice = string.Empty;
        row.Note = note;
        Toast(note.Length == 0 ? "Note removed." : "Note saved.");
    }

    [RelayCommand]
    private void CopyRowTxId(HistoryRow? row)
    {
        if (row is not null)
        {
            Copy(row.TxId, "Transaction ID");
        }
    }

    /// <summary>Prove an outgoing payment from History: open Tools with it filled in.</summary>
    [RelayCommand]
    private async Task ProveRowAsync(HistoryRow? row)
    {
        if (row is null || !row.IsOutgoing)
        {
            return;
        }

        VerifyTxId = row.TxId;
        VerifyAddress = row.Entry.Destinations is { Count: 1 } d ? d[0].Address : string.Empty;
        VerifyResult = string.Empty;
        SelectedTab = 4;
        await FetchTxKeyAsync();
    }

    /// <summary>Raised by Export CSV: the view shows the save dialog and writes <see cref="BuildHistoryCsv"/>.</summary>
    public event Action? ExportHistoryRequested;

    [RelayCommand]
    private void ExportHistory() => ExportHistoryRequested?.Invoke();

    /// <summary>History as CSV (spreadsheet-friendly), notes included. Every row of the account,
    /// whatever the filter shows.</summary>
    public string BuildHistoryCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("date,type,amount_xmr,fee_xmr,height,txid,note");
        foreach (TransferEntry t in _entries)
        {
            // txid/type/date contain no commas or quotes (hex, fixed words, fixed format); the note can.
            string note = _secrets.TxNotes.GetValueOrDefault(t.TxId) ?? string.Empty;
            sb.Append(t.Date).Append(',')
              .Append(t.Type).Append(',')
              .Append(MoneroRpcClient.AtomicToXmr(t.Amount).ToString("0.############", CultureInfo.InvariantCulture)).Append(',')
              .Append(MoneroRpcClient.AtomicToXmr(t.Fee).ToString("0.############", CultureInfo.InvariantCulture)).Append(',')
              .Append(t.Height).Append(',')
              .Append(t.TxId).Append(',')
              .Append(CsvField(note)).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>RFC 4180 quoting, plus a leading quote for anything a spreadsheet would run as a
    /// formula (=, +, -, @): a note is user text and must not become one.</summary>
    internal static string CsvField(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        string v = value is ['=' or '+' or '-' or '@' or '\t' or '\r', ..] ? "'" + value : value;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
