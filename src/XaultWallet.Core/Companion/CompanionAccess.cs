namespace XaultWallet.Core.Companion;

/// <summary>What the app does with a request on the local channel, before looking at what it asks.</summary>
public enum CompanionAccessDecision
{
    /// <summary>Answer it.</summary>
    Allow,

    /// <summary>Refuse it: the user hasn't connected this browser in Settings (or the request is a kind the
    /// sender can't make).</summary>
    NotConnected,
}

/// <summary>
/// Who may use the companion channel. The channel is open whenever the app runs, because a second launch
/// (a clicked monero: link, or starting XaultWallet again) hands its link or "show" over through it. So:
/// <list type="bullet">
/// <item>a link hand-off ("via" link) may only open a payment request or bring the window forward, whatever
/// the settings say: it can never ask for the state or an address;</item>
/// <item>everything from a browser (status, show, pay, receive) is answered only for a browser the user
/// connected in Settings. Off by default: until then the channel answers browsers with "not-connected".</item>
/// </list>
/// "via" is what the native host (or the second launch) says; anything running as the user can say the
/// same, so this narrows what an unconnected install exposes, it doesn't authenticate the sender.
/// </summary>
public static class CompanionAccess
{
    /// <summary>The settings key of a browser (as <c>AppSettings.ConnectedBrowsers</c> holds it), or null for a link.</summary>
    public static string? BrowserKey(CompanionCaller via) => via switch
    {
        CompanionCaller.Chrome => "chrome",
        CompanionCaller.Firefox => "firefox",
        _ => null,
    };

    public static CompanionAccessDecision Decide(CompanionRequest request, IReadOnlyCollection<string> connectedBrowsers)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connectedBrowsers);
        if (request.Via == CompanionCaller.Link)
        {
            return request.Type is CompanionRequestType.Pay or CompanionRequestType.Show
                ? CompanionAccessDecision.Allow
                : CompanionAccessDecision.NotConnected;
        }

        return BrowserKey(request.Via) is { } key && connectedBrowsers.Contains(key)
            ? CompanionAccessDecision.Allow
            : CompanionAccessDecision.NotConnected;
    }
}
