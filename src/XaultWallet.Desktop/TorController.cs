using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Tor;

namespace XaultWallet.Desktop;

/// <summary>Where the app's built-in Tor is.</summary>
public enum TorState
{
    Off,
    Starting,
    Ready,
    Failed,
}

/// <summary>Built-in Tor isn't connected, so traffic that must go through it can't be sent.</summary>
public sealed class TorNotReadyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The app's own Tor, Wasabi-style: started when the app starts (if the user turned it on), kept
/// running while the app runs, and the only route node traffic may take while it is on. Owns one
/// <see cref="TorProcess"/> at a time on a SOCKS port chosen once per run, so a restart doesn't
/// strand a wallet backend that was started with the old port.
///
/// Every property changes on the UI thread (bound by Settings and the wallet's route badge).
/// </summary>
public sealed partial class TorController : ObservableObject
{
    private readonly Func<string> _installRoot;
    private readonly Func<string> _dataDirectory;
    private readonly Func<string> _configuredBinary;
    private TorProcess? _process;
    private Task<string>? _starting;
    private int _port;
    private bool _stopping;

    internal TorController(Func<string> installRoot, Func<string> dataDirectory, Func<string> configuredBinary)
    {
        _installRoot = installRoot;
        _dataDirectory = dataDirectory;
        _configuredBinary = configuredBinary;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsStarting))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private TorState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _detail = string.Empty;

    /// <summary>"127.0.0.1:port" while connected.</summary>
    [ObservableProperty] private string _socksAddress = string.Empty;

    public bool IsReady => State == TorState.Ready;

    public bool IsStarting => State == TorState.Starting;

    public string StatusText => State switch
    {
        TorState.Starting => $"Connecting to Tor… {Percent}%" + (Detail.Length > 0 ? $" · {Detail}" : string.Empty),
        TorState.Ready => $"Connected to Tor · SOCKS {SocksAddress}",
        TorState.Failed => Detail,
        _ => "Tor is off.",
    };

    /// <summary>The tor binary to run: the one set in Settings, else the one XaultWallet installed,
    /// else tor on PATH; empty when there is none.</summary>
    public string ResolveBinary()
    {
        string configured = _configuredBinary().Trim();
        if (configured.Length > 0)
        {
            return ExecutableLocator.ResolveConfigured(configured, Environment.GetEnvironmentVariable("PATH"));
        }

        return TorInstaller.FindInstalled(_installRoot())
               ?? ExecutableLocator.FindOnPath(TorInstaller.TorFileName, Environment.GetEnvironmentVariable("PATH"))
               ?? string.Empty;
    }

    /// <summary>Start Tor if it isn't running and wait until it is connected.</summary>
    /// <returns>Its SOCKS address.</returns>
    /// <exception cref="TorNotReadyException">It couldn't connect; the message says why.</exception>
    public Task<string> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.InvokeAsync(() => EnsureStartedAsync(ct));
        }

        if (State == TorState.Ready && _process is { HasExited: false })
        {
            return Task.FromResult(SocksAddress);
        }

        _starting ??= StartCoreAsync();
        return _starting.WaitAsync(ct);
    }

    private async Task<string> StartCoreAsync()
    {
        // Never complete synchronously: the caller stores this task in _starting, which the finally
        // below clears; a synchronous failure would clear it before it was stored.
        await Task.Yield();
        try
        {
            string binary = ResolveBinary();
            if (binary.Length == 0)
            {
                throw new TorNotReadyException("Tor isn't installed. Download & install it under Settings → Network & privacy, or choose your own tor.");
            }

            _stopping = false;
            State = TorState.Starting;
            Percent = 0;
            Detail = string.Empty;

            var process = new TorProcess(binary, _dataDirectory(), _port);
            _port = process.SocksPort; // the same port for the rest of this run
            process.Exited += () => Dispatcher.UIThread.Post(() => OnExited(process));
            _process = process;

            var progress = new Progress<TorBootstrap>(b =>
            {
                Percent = b.Percent;
                Detail = b.Summary;
            });
            try
            {
                await process.StartAsync(progress, TimeSpan.FromMinutes(3), CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
            {
                _process = null;
                throw new TorNotReadyException(ex.Message, ex);
            }

            SocksAddress = process.SocksAddress;
            Detail = string.Empty;
            State = TorState.Ready;
            Log.Info("Built-in Tor connected.");
            return SocksAddress;
        }
        catch (TorNotReadyException ex)
        {
            State = TorState.Failed;
            Detail = ex.Message;
            Log.Warn("Built-in Tor didn't connect: " + ex.Message);
            throw;
        }
        finally
        {
            _starting = null;
        }
    }

    private void OnExited(TorProcess process)
    {
        if (!ReferenceEquals(process, _process) || _stopping)
        {
            return;
        }

        _process = null;
        SocksAddress = string.Empty;
        State = TorState.Failed;
        Detail = "Tor stopped unexpectedly. Node traffic is held back until it runs again: Restart Tor in Settings.";
        Log.Warn("Built-in Tor exited unexpectedly.");
    }

    /// <summary>A state to show with no tor behind it (UI snapshots and tests only).</summary>
    internal void ShowForPreview(TorState state, int percent = 0, string detail = "", string socks = "")
    {
        State = state;
        Percent = percent;
        Detail = detail;
        SocksAddress = socks;
    }

    /// <summary>Stop Tor (turned off, or the app is exiting).</summary>
    public async Task StopAsync()
    {
        _stopping = true;
        TorProcess? p = _process;
        _process = null;
        if (p is not null)
        {
            await p.StopAsync();
        }

        SocksAddress = string.Empty;
        Detail = string.Empty;
        State = TorState.Off;
    }

    /// <summary>Stop and start again on the same port.</summary>
    public async Task<string> RestartAsync()
    {
        await StopAsync();
        return await EnsureStartedAsync();
    }
}
