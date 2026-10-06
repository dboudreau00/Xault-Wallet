using System.Text;

namespace XaultWallet.Core.Monero;

/// <summary>A payment request: who to pay and, optionally, how much and what for.</summary>
public sealed record MoneroPaymentRequest(string Address, decimal? Amount = null, string Description = "", string RecipientName = "");

/// <summary>
/// "monero:" URIs (the format Monero wallets put in payment QR codes and links):
/// <c>monero:&lt;address&gt;?tx_amount=1.25&amp;tx_description=Rent%20July&amp;recipient_name=Alice</c>.
/// Amounts are XMR with a dot as the decimal separator. Payment IDs (tx_payment_id) are a legacy
/// feature integrated addresses replaced; a URI carrying one is refused rather than silently
/// paid without it.
/// </summary>
public static partial class MoneroUri
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9]{1,8}(\.[0-9]{1,12})?$")]
    private static partial System.Text.RegularExpressions.Regex StrictAmount();

    private const string Scheme = "monero:";

    /// <summary>Build a URI for <paramref name="request"/>. The address is taken as is (validate it first).</summary>
    public static string Build(MoneroPaymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sb = new StringBuilder(Scheme).Append(request.Address.Trim());
        char sep = '?';
        void Add(string name, string value)
        {
            sb.Append(sep).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            sep = '&';
        }

        if (request.Amount is { } amount && amount > 0m)
        {
            Add("tx_amount", XmrAmount.Format(amount));
        }

        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            Add("tx_description", request.Description.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.RecipientName))
        {
            Add("recipient_name", request.RecipientName.Trim());
        }

        return sb.ToString();
    }

    /// <summary>True when <paramref name="text"/> looks like a monero: URI (so a Send field can tell a
    /// pasted payment link from a plain address).</summary>
    public static bool IsUri(string? text) =>
        (text ?? string.Empty).TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse a monero: URI. Returns null and a reason when it isn't one this app can pay.</summary>
    public static MoneroPaymentRequest? TryParse(string? uri, out string? problem)
    {
        problem = null;
        string s = (uri ?? string.Empty).Trim();
        if (!IsUri(s))
        {
            problem = "That isn't a monero: payment link.";
            return null;
        }

        s = s[Scheme.Length..];
        if (s.StartsWith("//", StringComparison.Ordinal))
        {
            s = s[2..]; // tolerate "monero://"
        }

        int q = s.IndexOf('?');
        string address = (q < 0 ? s : s[..q]).Trim();
        if (address.Length == 0)
        {
            problem = "The payment link has no address.";
            return null;
        }

        if (address.Contains(';', StringComparison.Ordinal))
        {
            problem = "Payment links to several addresses aren't supported. Ask for one address per payment.";
            return null;
        }

        decimal? amount = null;
        string description = string.Empty;
        string recipient = string.Empty;
        if (q >= 0)
        {
            foreach (string pair in s[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string name = (eq < 0 ? pair : pair[..eq]).Trim().ToLowerInvariant();
                string value;
                try
                {
                    value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    problem = "The payment link is malformed.";
                    return null;
                }

                switch (name)
                {
                    case "tx_amount":
                        // Strict, unlike a typed amount: digits and one optional dot. A link's "1,500"
                        // could mean 1500 or 1.5 — guessing could underpay a thousandfold.
                        if (!StrictAmount().IsMatch(value.Trim())
                            || !decimal.TryParse(value.Trim(), System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out decimal parsed)
                            || parsed <= 0m || parsed > MoneroRpcClient.MaxXmrAmount)
                        {
                            problem = "The payment link's amount isn't a valid XMR amount.";
                            return null;
                        }

                        amount = parsed;
                        break;
                    case "tx_description":
                        description = value.Trim();
                        break;
                    case "recipient_name":
                        recipient = value.Trim();
                        break;
                    case "tx_payment_id":
                        if (value.Trim().Length > 0)
                        {
                            problem = "This payment link uses a payment ID, which this wallet doesn't send. Ask the recipient for an integrated address or a subaddress instead.";
                            return null;
                        }

                        break;
                    default:
                        break; // unknown parameters are ignored, as the format allows
                }
            }
        }

        return new MoneroPaymentRequest(address, amount, description, recipient);
    }
}
