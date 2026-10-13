namespace XaultWallet.Core.Companion;

/// <summary>
/// XaultWallet started by a browser as the companion extension's native messaging host. It reads the
/// extension's messages from standard input, checks each one, passes it to the running app over the
/// local channel and writes the app's reply back, rebuilt from the fields that kind of request answers
/// with. It holds no wallet and opens nothing: the app decides everything. For a payment request or
/// "show" while the app isn't running, it starts the app first.
/// </summary>
public sealed class NativeHost
{
    private readonly CompanionCaller _caller;
    private readonly Func<byte[], CancellationToken, Task<byte[]?>> _forward;
    private readonly Func<bool> _launchApp;
    private readonly Func<bool> _appRunning;
    private readonly TimeSpan _startWait;

    /// <param name="caller">The browser that started this host (see <see cref="TryIdentifyCaller"/>).</param>
    /// <param name="forward">Sends a request line to the app; null when no app is listening.</param>
    /// <param name="launchApp">Starts the app; false when it couldn't be started.</param>
    /// <param name="startWait">How long to wait for a just-started app to listen.</param>
    /// <param name="appRunning">Whether a copy of the app is running (it may still be starting, or be
    /// busy): then it is waited for, never started a second time.</param>
    public NativeHost(CompanionCaller caller, Func<byte[], CancellationToken, Task<byte[]?>> forward, Func<bool> launchApp, TimeSpan? startWait = null, Func<bool>? appRunning = null)
    {
        _caller = caller;
        _forward = forward ?? throw new ArgumentNullException(nameof(forward));
        _launchApp = launchApp ?? throw new ArgumentNullException(nameof(launchApp));
        _appRunning = appRunning ?? (() => false);
        _startWait = startWait ?? TimeSpan.FromSeconds(25);
    }

    /// <summary>
    /// Whether these are the arguments a browser starts a native messaging host with, and which allowed
    /// extension asked. Chrome passes the caller's origin ("chrome-extension://&lt;id&gt;/", then on Windows
    /// "--parent-window=&lt;handle&gt;"); Firefox passes the path of the host manifest and the extension's id.
    /// Null for any other command line, including a browser launch for an extension that isn't ours: the
    /// browser checks the manifest's list too, and this is the second check.
    /// </summary>
    public static CompanionCaller? TryIdentifyCaller(IReadOnlyList<string> args, out bool looksLikeHostLaunch)
    {
        ArgumentNullException.ThrowIfNull(args);
        looksLikeHostLaunch = false;
        if (args.Count is >= 1 and <= 2 && args[0].StartsWith("chrome-extension://", StringComparison.Ordinal))
        {
            looksLikeHostLaunch = true;
            if (args.Count == 2 && !args[1].StartsWith("--parent-window=", StringComparison.Ordinal))
            {
                return null;
            }

            string origin = args[0];
            return BrowserRegistration.ChromeExtensionIds.Any(id => string.Equals(origin, "chrome-extension://" + id + "/", StringComparison.Ordinal))
                ? CompanionCaller.Chrome
                : null;
        }

        if (args.Count == 2 && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && args[1].Contains('@', StringComparison.Ordinal))
        {
            looksLikeHostLaunch = true;
            return BrowserRegistration.FirefoxExtensionIds.Contains(args[1], StringComparer.Ordinal) ? CompanionCaller.Firefox : null;
        }

        return null;
    }

    /// <summary>Serve messages until the browser closes standard input (or stops reading the replies).
    /// Returns the process exit code.</summary>
    public async Task<int> RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? message;
                try
                {
                    message = await NativeMessaging.ReadAsync(input, ct).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    // The framing is lost: answer once, then stop.
                    await NativeMessaging.WriteAsync(output, CompanionProtocol.Fail(string.Empty, new CompanionError(CompanionErrors.TooLarge, "The message is too large or cut short.")), ct).ConfigureAwait(false);
                    return 1;
                }

                if (message is null)
                {
                    return 0;
                }

                byte[] reply = await HandleAsync(message, ct).ConfigureAwait(false);
                await NativeMessaging.WriteAsync(output, reply, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return 1; // the browser closed the popup (and the pipes) before the answer came: nobody to tell
        }

        return 0;
    }

    /// <summary>One message from the browser to its reply. Never throws: whatever goes wrong becomes a
    /// refusal with a fixed sentence (no exception text reaches the browser).</summary>
    public async Task<byte[]> HandleAsync(byte[] message, CancellationToken ct = default)
    {
        CompanionRequest? request = CompanionProtocol.Parse(message, _caller, out CompanionError? error);
        if (request is null)
        {
            return CompanionProtocol.Fail(CompanionProtocol.TryReadId(message), error!);
        }

        byte[] line = CompanionProtocol.Serialize(request);
        try
        {
            byte[]? reply = await _forward(line, ct).ConfigureAwait(false);
            bool running = reply is null && _appRunning();
            if (reply is null && request.Type is CompanionRequestType.Pay or CompanionRequestType.Show)
            {
                reply = await StartAppAndForwardAsync(line, launch: !running, ct).ConfigureAwait(false);
            }

            if (reply is null)
            {
                return CompanionProtocol.Fail(request.Id, running || _appRunning()
                    ? new CompanionError(CompanionErrors.NotReady, "XaultWallet is busy or still starting. Try again in a moment.")
                    : new CompanionError(CompanionErrors.NotRunning, "XaultWallet isn't running."));
            }

            return CompanionProtocol.RebuildReply(reply, request)
                   ?? CompanionProtocol.Fail(request.Id, new CompanionError(CompanionErrors.Failed, "XaultWallet sent a reply the companion can't use."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return CompanionProtocol.Fail(request.Id, new CompanionError(CompanionErrors.Failed, "XaultWallet didn't answer. Try again; if it keeps failing, restart XaultWallet."));
        }
    }

    /// <summary>Starts the app (unless a copy is already running) and passes the request once it listens.</summary>
    private async Task<byte[]?> StartAppAndForwardAsync(byte[] line, bool launch, CancellationToken ct)
    {
        if (launch && !_launchApp())
        {
            return null;
        }

        long until = Environment.TickCount64 + (long)_startWait.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            if (await _forward(line, ct).ConfigureAwait(false) is { } reply)
            {
                return reply;
            }
        }

        return null;
    }
}
