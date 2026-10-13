using System.IO.Pipes;
using XaultWallet.Core.Companion;
using XaultWallet.Core.Diagnostics;

namespace XaultWallet.Desktop.Companion;

/// <summary>
/// Listens on the app's <see cref="CompanionEndpoint"/> for the browser companion's native host and
/// for monero: links handed to a second launch. One request per connection; every request is parsed
/// and checked again here (the other end is any program running as the user, not necessarily ours).
/// </summary>
internal sealed class CompanionServer : IAsyncDisposable
{
    private const int MaxConnections = 4;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly CompanionEndpoint _endpoint;
    private readonly Func<CompanionRequest, Task<byte[]>> _handle;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private Task _loop = Task.CompletedTask;

    /// <param name="handle">Answers one checked request (it runs the work on the UI thread itself).</param>
    public CompanionServer(CompanionEndpoint endpoint, Func<CompanionRequest, Task<byte[]>> handle)
    {
        _endpoint = endpoint;
        _handle = handle;
    }

    /// <summary>Raised once when the channel can't be opened because another program already holds its
    /// name: something may be trying to answer in XaultWallet's place.</summary>
    public event Action? NameTaken;

    public void Start()
    {
        _endpoint.RemoveLeftoverSocket();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        bool first = true;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // The slot is held from here until the connection is served, or given back below.
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = _endpoint.CreateServer(MaxConnections, first);
                first = false;
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                failures = 0;
                NamedPipeServerStream connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                GiveBack(pipe);
                return;
            }
            catch (UnauthorizedAccessException) when (first)
            {
                GiveBack(pipe);
                Log.Warn("The companion channel's name is already taken by another program; browser requests and monero: links are not accepted this run.");
                NameTaken?.Invoke();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GiveBack(pipe);
                Log.WarnOnce("companion-listen", "The companion channel stopped listening for a moment: " + ex.GetType().Name);
                if (++failures > 8)
                {
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(failures), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>A pipe that never got to serve: closed, and its slot given back.</summary>
    private void GiveBack(NamedPipeServerStream? pipe)
    {
        pipe?.Dispose();
        _slots.Release();
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            byte[]? line;
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                read.CancelAfter(ReadTimeout);
                line = await CompanionWire.ReadLineAsync(pipe, CompanionProtocol.MaxRequestBytes, read.Token).ConfigureAwait(false);
            }

            if (line is null)
            {
                return;
            }

            CompanionRequest? request = CompanionProtocol.Parse(line, via: null, out CompanionError? error);
            byte[] reply = request is null
                ? CompanionProtocol.Fail(CompanionProtocol.TryReadId(line), error!)
                : await _handle(request).ConfigureAwait(false);
            await CompanionWire.WriteLineAsync(pipe, reply, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            // The caller went away, sent too much, or took too long: nothing to answer.
        }
        catch (Exception ex)
        {
            Log.ErrorOnce("companion-serve", "A companion request failed", ex);
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            _slots.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
