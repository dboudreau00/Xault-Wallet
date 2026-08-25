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
}
