namespace XaultWallet.Core.Monero;

/// <summary>
/// The single definition of "a valid daemon (node) address" — an absolute http(s) URL.
/// Previously duplicated across VaultManager, MoneroProcessManager, and the create screen;
/// one helper keeps the rule identical everywhere it is enforced.
/// </summary>
public static class DaemonAddress
{
    public static bool IsValid(string? address) => TryParse(address, out _);

    public static bool TryParse(string? address, out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(address)
            && Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    /// <summary>
    /// True when the address points at THIS machine: "localhost" or a loopback IP (127.0.0.0/8, ::1).
    /// Only such a node can be the user's own private test chain; nothing on the network qualifies.
    /// </summary>
    public static bool IsLoopback(string? address)
    {
        if (!TryParse(address, out Uri uri))
        {
            return false;
        }

        string host = uri.IdnHost.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? ip) && System.Net.IPAddress.IsLoopback(ip));
    }
}
