namespace XaultWallet.Desktop;

/// <summary>
/// Work that starts a monero-wallet-rpc of its own for a moment, outside any open wallet: a seed
/// being generated, an import being checked. Each runs under a token that the app's exit cancels,
/// and that its owner (the open vault, for the add-wallet screen) cancels when it is locked; both
/// then wait for the work to end, which stops its backend and shreds its folder. Without this a
/// generation or check started just before a lock or exit ran on, backend and all, after the
/// screen that started it was gone.
/// </summary>
public sealed class TemporaryBackends
{
    private readonly CancellationTokenSource _exit = new();
    private readonly object _gate = new();
    private readonly List<(Task Work, CancellationToken Owner)> _running = new();

    /// <summary>Run <paramref name="work"/> under a token cancelled by the app's exit or by
    /// <paramref name="owner"/>. Throws <see cref="OperationCanceledException"/> if either already is.</summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken owner = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(owner, _exit.Token);
        linked.Token.ThrowIfCancellationRequested();
        Task<T> task = work(linked.Token);
        (Task, CancellationToken) entry = (task, owner);
        lock (_gate)
        {
            _running.Add(entry);
        }

        try
        {
            return await task;
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(entry);
            }
        }
    }

    /// <summary>Wait for the work started for <paramref name="owner"/> to end (its owner cancels it first).</summary>
    public Task WhenEndedAsync(CancellationToken owner) => WhenEndedAsync(r => r.Owner == owner);

    /// <summary>The app is exiting: cancel all of it and wait for it to end.</summary>
    public Task StopAllAsync()
    {
        _exit.Cancel();
        return WhenEndedAsync(_ => true);
    }

    private async Task WhenEndedAsync(Func<(Task Work, CancellationToken Owner), bool> which)
    {
        Task[] work;
        lock (_gate)
        {
            work = _running.Where(which).Select(r => r.Work).ToArray();
        }

        foreach (Task t in work)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch
            {
                // Its outcome is reported to whoever started it; here it only has to have ended.
            }
        }
    }
}
