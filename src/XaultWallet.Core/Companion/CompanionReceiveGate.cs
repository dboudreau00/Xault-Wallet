namespace XaultWallet.Core.Companion;

/// <summary>
/// A browser's "receive" makes a new subaddress and records it in the vault, so it waits for the user:
/// the first request only asks (the app shows "Give address"); once the user allows it in the app (a click:
/// no key presses the button, and with Enhanced Security on another program's input is dropped), the next request from the same site, for the same wallet,
/// within <see cref="Lifetime"/>, gets its address. One allowance, one address. Not thread-safe: the app
/// uses it on the UI thread.
/// </summary>
public sealed class CompanionReceiveGate
{
    /// <summary>How long a question waits for the user, and an allowance for the browser.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly Func<long> _now;
    private (object Owner, string Host, long Since)? _asked;
    private (object Owner, string Host, long Since)? _allowed;

    /// <param name="now">Milliseconds from a steady clock (Environment.TickCount64 by default).</param>
    public CompanionReceiveGate(Func<long>? now = null) => _now = now ?? (() => Environment.TickCount64);

    /// <summary>The site whose request waits for the user, or null.</summary>
    public string? AskedHost => _asked is { } a && Fresh(a.Since) ? a.Host : null;

    /// <summary>A request from <paramref name="host"/> for <paramref name="owner"/>'s wallet: true (and the
    /// allowance is used up) when the user allowed exactly this; otherwise it becomes the question waiting
    /// for the user (in place of any earlier one) and false.</summary>
    public bool TryTake(object owner, string host)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(host);
        if (_allowed is { } a && ReferenceEquals(a.Owner, owner) && string.Equals(a.Host, host, StringComparison.Ordinal) && Fresh(a.Since))
        {
            _allowed = null;
            return true;
        }

        _asked = (owner, host, _now());
        return false;
    }

    /// <summary>The user allowed the waiting question: true when there was a fresh one.</summary>
    public bool Allow()
    {
        if (_asked is not { } a || !Fresh(a.Since))
        {
            _asked = null;
            return false;
        }

        _asked = null;
        _allowed = (a.Owner, a.Host, _now());
        return true;
    }

    /// <summary>The user dismissed the question (or the app locked): nothing waits, nothing is allowed.</summary>
    public void Clear()
    {
        _asked = null;
        _allowed = null;
    }

    private bool Fresh(long since) => _now() - since <= (long)Lifetime.TotalMilliseconds;

    /// <summary>What the new address is labelled after the "Browser: " prefix. Unless the user turned on
    /// saving site names (Settings), nothing the browser sent goes into the vault: the label is empty and
    /// the address is just "Browser". With it on: what the user typed in the extension, or else the site.</summary>
    public static string LabelFor(string label, string origin, bool saveSiteNames)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (!saveSiteNames)
        {
            return string.Empty;
        }

        string host = CompanionProtocol.HostOf(origin);
        return label.Length > 0 ? label : host.Length > 0 ? host : "no page";
    }
}
