using Avalonia.Threading;

namespace XaultWallet.Desktop.Controls;

/// <summary>
/// The guard "Send now" uses (WalletViewModel.ConfirmHoldTime), for any button that appears because of
/// something from outside: for a moment after it appears it ignores clicks, so a click meant for whatever
/// was under the pointer, or one timed by another program that just brought the window forward, can't land
/// on it. <see cref="Held"/> is true during that moment (by the clock, so a command can rely on it); bind the
/// button's IsEnabled to its inverse (refreshed by a timer when the moment ends) and check it in the command
/// too (Enter or an access key would skip the binding).
/// </summary>
internal sealed class ClickHold
{
    private readonly Action _changed;
    private readonly Func<long> _now;
    private DispatcherTimer? _timer;
    private long _until;

    /// <param name="changed">Told when <see cref="Held"/> starts or ends (to raise the bound property).</param>
    /// <param name="now">Milliseconds from a steady clock (Environment.TickCount64 by default).</param>
    public ClickHold(Action changed, Func<long>? now = null)
    {
        _changed = changed;
        _now = now ?? (() => Environment.TickCount64);
        _until = long.MinValue;
    }

    public bool Held => _now() < _until;

    /// <summary>Hold from now (again, if already holding).</summary>
    public void Start()
    {
        _timer?.Stop();
        _until = _now() + (long)ViewModels.WalletViewModel.ConfirmHoldTime.TotalMilliseconds;
        _changed();
        _timer = new DispatcherTimer { Interval = ViewModels.WalletViewModel.ConfirmHoldTime };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            _changed();
        };
        _timer.Start();
    }
}
