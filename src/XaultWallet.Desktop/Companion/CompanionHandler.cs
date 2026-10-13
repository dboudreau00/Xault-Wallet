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

    public CompanionHandler(MainWindowViewModel shell, Action bringToFront)
    {
        _shell = shell;
        _bringToFront = bringToFront;
    }

    public Task<byte[]> HandleAsync(CompanionRequest request) =>
        Dispatcher.UIThread.InvokeAsync(() => HandleOnUiThreadAsync(request));

    private async Task<byte[]> HandleOnUiThreadAsync(CompanionRequest r)
    {
        switch (r.Type)
        {
            case CompanionRequestType.Status:
                return Status(r.Id);

            case CompanionRequestType.Show:
                _bringToFront();
                return CompanionProtocol.Ok(r.Id);

            case CompanionRequestType.Pay:
                if (!_pay.TryTake())
                {
                    return Fail(r.Id, CompanionErrors.TooMany, "Too many payment requests in a minute. Wait a moment.");
                }

                PaymentOffer offer = _shell.OfferPayment(new OutsidePayment(r.Uri, r.Via, r.Origin));
                if (offer is PaymentOffer.Shown or PaymentOffer.AfterUnlock or PaymentOffer.Waiting)
                {
                    _bringToFront();
                }

                return offer switch
                {
                    PaymentOffer.Shown => CompanionProtocol.Ok(r.Id, w => w.WriteString("result", "shown")),
                    PaymentOffer.AfterUnlock => CompanionProtocol.Ok(r.Id, w => w.WriteString("result", "after-unlock")),
                    PaymentOffer.Waiting => CompanionProtocol.Ok(r.Id, w => w.WriteString("result", "waiting")),
                    PaymentOffer.Busy => Fail(r.Id, CompanionErrors.Busy, "A payment is being confirmed in XaultWallet. Finish or cancel it first."),
                    _ => Fail(r.Id, CompanionErrors.NoVault, "XaultWallet has no wallet yet. Set it up first."),
                };

            default:
                if (!_receive.TryTake())
                {
                    return Fail(r.Id, CompanionErrors.TooMany, "Too many new addresses in a minute. Wait a moment.");
                }

                CompanionAddress result = await _shell.AddressForCompanionAsync(r.Label, r.Origin);
                return result.Address is { } address
                    ? CompanionProtocol.Ok(r.Id, w =>
                    {
                        w.WriteString("address", address);
                        w.WriteString("network", result.Network);
                    })
                    : CompanionProtocol.Fail(r.Id, result.Error!);
        }
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

    private static byte[] Fail(string id, string code, string message) => CompanionProtocol.Fail(id, new CompanionError(code, message));
}

/// <summary>At most <c>count</c> takes per <c>window</c>, sliding.</summary>
internal sealed class RateLimit(int count, TimeSpan window)
{
    private readonly Queue<DateTime> _taken = new();

    public bool TryTake(DateTime? now = null)
    {
        DateTime t = now ?? DateTime.UtcNow;
        while (_taken.Count > 0 && t - _taken.Peek() >= window)
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
