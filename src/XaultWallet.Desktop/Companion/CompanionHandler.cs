using System.Reflection;
using Avalonia.Threading;
using XaultWallet.Core.Companion;
using XaultWallet.Desktop.ViewModels;

namespace XaultWallet.Desktop.Companion;

/// <summary>
/// Answers the browser companion and monero: links, on the UI thread, from what the shell shows. It
/// hands out as little as it can: whether the vault is locked, the network of the wallet on screen, and
/// on request a new receive address. Never a balance, a wallet's name, history, keys or a seed; and a
/// payment request only ever opens in the Send tab, for the user to review and confirm in the app.
/// </summary>
internal sealed class CompanionHandler
{
    internal static readonly string AppVersion =
        (typeof(CompanionHandler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty).Split('+')[0];

    private readonly MainWindowViewModel _shell;
    private readonly Action _bringToFront;

    // A few a minute is plenty for a person; a program asking more is refused.
    private readonly RateLimit _receive = new(5, TimeSpan.FromMinutes(1));
    private readonly RateLimit _pay = new(6, TimeSpan.FromMinutes(1));
    private readonly RateLimit _show = new(10, TimeSpan.FromMinutes(1));
    private readonly RateLimit _status = new(60, TimeSpan.FromMinutes(1));

    /// <summary>The window is brought forward at most this often, whatever asks.</summary>
    private static readonly TimeSpan FrontGap = TimeSpan.FromSeconds(2);

    private long _lastFront = long.MinValue / 2;

    public CompanionHandler(MainWindowViewModel shell, Action bringToFront)
    {
        _shell = shell;
        _bringToFront = bringToFront;
    }

    public Task<byte[]> HandleAsync(CompanionRequest request) =>
        Dispatcher.UIThread.InvokeAsync(() => HandleOnUiThreadAsync(request));

    private async Task<byte[]> HandleOnUiThreadAsync(CompanionRequest r)
    {
        // Off by default: a browser is answered only once the user connected it in Settings; a link
        // hand-off from a second launch only ever opens a payment request or the window (CompanionAccess).
        if (CompanionAccess.Decide(r, AppServices.Instance.Settings.ConnectedBrowsers) != CompanionAccessDecision.Allow)
        {
            return Fail(r.Id, CompanionErrors.NotConnected, r.Via == CompanionCaller.Link
                ? "XaultWallet doesn't answer that from a link."
                : "This browser isn't connected to XaultWallet. Connect it in XaultWallet's Settings (Browser companion), then try again.");
        }

        switch (r.Type)
        {
            case CompanionRequestType.Status:
                return _status.TryTake() ? Status(r.Id) : TooMany(r.Id);

            case CompanionRequestType.Show:
                if (!_show.TryTake())
                {
                    return TooMany(r.Id);
                }

                BringToFront();
                return CompanionProtocol.Ok(r.Id);

            case CompanionRequestType.Pay:
                if (!_pay.TryTake())
                {
                    return Fail(r.Id, CompanionErrors.TooMany, "Too many payment requests in a minute. Wait a moment.");
                }

                var payment = new OutsidePayment(r.Uri, r.Via, r.Origin);
                PaymentOffer offer = _shell.OfferPayment(payment);
                if (offer is PaymentOffer.Shown or PaymentOffer.AfterUnlock or PaymentOffer.Waiting or PaymentOffer.Held)
                {
                    BringToFront();
                }
                else if (offer is not PaymentOffer.NoVault)
                {
                    _shell.PaymentNotOpened(payment, offer); // the app says so too: a link click has no other answer
                }

                return offer switch
                {
                    PaymentOffer.Shown => CompanionProtocol.Ok(r.Id, w => w.WriteString("result", "shown")),
                    PaymentOffer.AfterUnlock => CompanionProtocol.Ok(r.Id, w => w.WriteString("result", "after-unlock")),
                    PaymentOffer.Waiting => CompanionProtocol.Ok(r.Id, w => w.WriteString("result", "waiting")),
                    PaymentOffer.Held => CompanionProtocol.Ok(r.Id, w => w.WriteString("result", "held")),
                    PaymentOffer.Confirming => Fail(r.Id, CompanionErrors.Busy, "A payment is being confirmed in XaultWallet. Finish or cancel it first."),
                    PaymentOffer.FormInUse => Fail(r.Id, CompanionErrors.Busy, "XaultWallet's Send form holds a payment you started. Send or clear it there first."),
                    PaymentOffer.CheckLastPayment => Fail(r.Id, CompanionErrors.Busy, "XaultWallet is showing a payment that may already have gone out. Check it there first."),
                    PaymentOffer.WatchOnly => Fail(r.Id, CompanionErrors.WatchOnly, "The wallet open in XaultWallet is watch-only: it can't pay."),
                    _ => Fail(r.Id, CompanionErrors.NoVault, "XaultWallet has no wallet yet. Set it up first."),
                };

            default:
                if (!_receive.TryTake())
                {
                    return Fail(r.Id, CompanionErrors.TooMany, "Too many new addresses in a minute. Wait a moment.");
                }

                CompanionAddress result = await _shell.AddressForCompanionAsync(r.Label, r.Origin);
                if (result.Error?.Code == CompanionErrors.Busy)
                {
                    BringToFront(); // it waits for the user in the app (Give address, or the wallet to be opened)
                }

                return result.Address is { } address
                    ? CompanionProtocol.Ok(r.Id, w =>
                    {
                        w.WriteString("address", address);
                        w.WriteString("network", result.Network);
                    })
                    : CompanionProtocol.Fail(r.Id, result.Error!);
        }
    }

    /// <summary>Forward, but never more often than every couple of seconds (a program asking in a loop
    /// would otherwise keep the window jumping in front of everything).</summary>
    private void BringToFront()
    {
        long now = Environment.TickCount64;
        if (now - _lastFront < (long)FrontGap.TotalMilliseconds)
        {
            return;
        }

        _lastFront = now;
        _bringToFront();
    }

    private byte[] Status(string id)
    {
        CompanionStatus s = _shell.StatusForCompanion();
        return CompanionProtocol.Ok(id, w =>
        {
            w.WriteString("app", "XaultWallet");
            w.WriteString("version", AppVersion);
            w.WriteString("state", s.State);
            if (s.Network is { } network)
            {
                w.WriteString("network", network);
                w.WriteBoolean("watchOnly", s.WatchOnly);
                w.WriteBoolean("ready", s.Ready);
            }
        });
    }

    private static byte[] TooMany(string id) => Fail(id, CompanionErrors.TooMany, "Too many requests in a minute. Wait a moment.");

    private static byte[] Fail(string id, string code, string message) => CompanionProtocol.Fail(id, new CompanionError(code, message));
}

/// <summary>At most <c>count</c> takes per <c>window</c>, sliding, on the system's monotonic clock (a
/// change of the computer's date or time doesn't open or close it).</summary>
internal sealed class RateLimit(int count, TimeSpan window)
{
    private readonly Queue<long> _taken = new();

    /// <param name="now">Milliseconds on the monotonic clock (tests pass their own).</param>
    public bool TryTake(long? now = null)
    {
        long t = now ?? Environment.TickCount64;
        while (_taken.Count > 0 && t - _taken.Peek() >= (long)window.TotalMilliseconds)
        {
            _taken.Dequeue();
        }

        if (_taken.Count >= count)
        {
            return false;
        }

        _taken.Enqueue(t);
        return true;
    }
}
