using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;

namespace XaultWallet.Core.Tor;

/// <summary>How far Tor has got connecting to the network.</summary>
/// <param name="Percent">0 to 100; 100 means circuits can be built.</param>
/// <param name="Summary">Tor's own words for the step ("Loading relay descriptors").</param>
public sealed record TorBootstrap(int Percent, string Summary);

/// <summary>
/// One tor child process run by the app, as a SOCKS proxy on a loopback port and nothing else:
/// a client only (never a relay), no control port, its own data directory, and the system's torrc
/// files ignored so nothing outside the app changes how it behaves.
///
/// It lives exactly as long as the app: tied to it on Windows by the kill-on-close job object, and
/// everywhere by Tor's own __OwningControllerProcess (Tor exits when the given process is gone).
/// <see cref="StartAsync"/> returns only once Tor reports "Bootstrapped 100%", so whoever routes
/// traffic through <see cref="SocksAddress"/> afterwards knows it can reach the network.
/// </summary>
public sealed partial class TorProcess : IAsyncDisposable
{
    private readonly string _torBinary;
    private readonly string _dataDirectory;
    private readonly ConcurrentQueue<string> _tail = new();
    private Process? _process;
    private const int TailMax = 40;

    /// <param name="torBinary">Full path to tor.</param>
    /// <param name="dataDirectory">Tor's DataDirectory (private to this user; kept between runs so a
    /// restart doesn't download the whole consensus again).</param>
    /// <param name="socksPort">Loopback port for the SOCKS listener; 0 = pick a free one.</param>
    public TorProcess(string torBinary, string dataDirectory, int socksPort = 0)
    {
        _torBinary = torBinary ?? throw new ArgumentNullException(nameof(torBinary));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        SocksPort = socksPort > 0 ? socksPort : FreeLocalPort();
    }

    public int SocksPort { get; }

    /// <summary>"127.0.0.1:&lt;port&gt;": what wallet-rpc's --proxy and the app's own requests use.</summary>
    public string SocksAddress => "127.0.0.1:" + SocksPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>Raised (on a thread-pool thread) when tor exits after it was started.</summary>
    public event Action? Exited;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process is null || _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Start tor and wait until it has bootstrapped (or failed, or <paramref name="timeout"/> passed).</summary>
    /// <exception cref="InvalidOperationException">tor exited or reported an error; the message quotes it.</exception>
    /// <exception cref="TimeoutException">Not bootstrapped in time.</exception>
    public async Task StartAsync(IProgress<TorBootstrap>? progress, TimeSpan timeout, CancellationToken ct)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Tor is already running.");
        }

        ExecutableLocator.EnsureLaunchable(_torBinary);
        PrivateFiles.EnsureDirectory(_dataDirectory);

        // Empty files stand in for the system-wide torrc and torrc-defaults, so a torrc installed by
        // something else (a system tor, a relay config) can't add listeners or change this tor.
        string emptyConfig = Path.Combine(_dataDirectory, "torrc.empty");
        if (!File.Exists(emptyConfig))
        {
            PrivateFiles.WriteAllText(emptyConfig, string.Empty);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _torBinary,
            WorkingDirectory = _dataDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        void Option(string name, string value)
        {
            psi.ArgumentList.Add(name);
            psi.ArgumentList.Add(value);
        }

        Option("--defaults-torrc", emptyConfig);
        Option("-f", emptyConfig);
        Option("--SocksPort", SocksAddress);
        Option("--ControlPort", "0");
        Option("--ORPort", "0");
        Option("--DataDirectory", _dataDirectory);
        Option("--ClientOnly", "1");
        Option("--AvoidDiskWrites", "1");
        Option("--SafeLogging", "1");
        Option("--Log", "notice stdout");
        Option("--__OwningControllerProcess", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        string dir = Path.GetDirectoryName(_torBinary) ?? string.Empty;
        foreach ((string option, string file) in new[] { ("--GeoIPFile", "geoip"), ("--GeoIPv6File", "geoip6") })
        {
            string path = Path.Combine(dir, file);
            if (File.Exists(path))
            {
                Option(option, path);
            }
        }

        AddLibraryPath(psi, _torBinary);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int refused = 0;
        void OnLine(string line)
        {
            Remember(line);
            if (ParseBootstrap(line) is { } step)
            {
                progress?.Report(step);
                if (step.Percent >= 100)
                {
                    ready.TrySetResult();
                }
            }
            else if (line.Contains("[err]", StringComparison.Ordinal))
            {
                ready.TrySetException(new InvalidOperationException("Tor stopped with an error: " + Clean(line)));
            }
            else if (IsConnectRefusedLocally(line) && Interlocked.Increment(ref refused) >= RefusalsBeforeGivingUp)
            {
                // The OS refused every outgoing connection (WSAEACCES / EACCES): a firewall or security
                // product is blocking this tor binary. Waiting longer can't help; say what to fix.
                ready.TrySetException(new InvalidOperationException(
                    $"Your firewall or security software is blocking tor from connecting. Allow \"{_torBinary}\" to make " +
                    "outgoing connections, then start Tor again."));
            }
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Tor did not start.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Couldn't run '{_torBinary}': {ex.Message}", ex);
        }

        _process = process;
        WindowsChildJob.TryAssign(process);
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                OnLine(e.Data);
            }
        };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data is not null)
            {
                Remember(e.Data);
            }
        };
        process.Exited += (sender, e) =>
        {
            ready.TrySetException(new InvalidOperationException("Tor exited before it connected. " + LastLines()));
            Exited?.Invoke();
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await ready.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await StopAsync().ConfigureAwait(false);
            throw new TimeoutException("Tor didn't finish connecting in time. " + LastLines());
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        Process? p = _process;
        _process = null;
        if (p is null)
        {
            return;
        }

        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await p.WaitForExitAsync(killTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.WarnOnce("tor-stop", "Error stopping tor: " + ex.GetType().Name);
        }
        finally
        {
            p.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// The Expert Bundle keeps its libraries next to tor (Linux: libevent, OpenSSL; macOS:
    /// libevent), as Tor Browser's own launcher expects. Point the loader at that folder, but only
    /// for a tor that has them beside it, never for a system tor.
    /// </summary>
    internal static void AddLibraryPath(ProcessStartInfo psi, string binaryPath)
    {
        string? dir = Path.GetDirectoryName(binaryPath);
        if (dir is null || OperatingSystem.IsWindows())
        {
            return;
        }

        string variable = OperatingSystem.IsMacOS() ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";
        string pattern = OperatingSystem.IsMacOS() ? "*.dylib" : "*.so*";
        try
        {
            if (Directory.EnumerateFiles(dir, pattern).Any())
            {
                psi.Environment[variable] = dir;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no libraries readable beside it: run it as it is
        }
    }

    /// <summary>How many relays must refuse locally before tor is declared blocked (it tries several at once).</summary>
    internal const int RefusalsBeforeGivingUp = 6;

    /// <summary>A bootstrap warning whose cause is the local OS refusing the connection.</summary>
    internal static bool IsConnectRefusedLocally(string line) =>
        line.Contains("Problem bootstrapping", StringComparison.Ordinal)
        && (line.Contains("WSAEACCES", StringComparison.Ordinal) || line.Contains("Permission denied", StringComparison.Ordinal));

    /// <summary>"… [notice] Bootstrapped 45% (loading_descriptors): Loading relay descriptors" → (45, "Loading relay descriptors").</summary>
    internal static TorBootstrap? ParseBootstrap(string line)
    {
        Match m = BootstrapLine().Match(line);
        if (!m.Success || !int.TryParse(m.Groups["pct"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pct) || pct > 100)
        {
            return null;
        }

        return new TorBootstrap(pct, m.Groups["summary"].Value.Trim());
    }

    private void Remember(string line)
    {
        _tail.Enqueue(line);
        while (_tail.Count > TailMax)
        {
            _tail.TryDequeue(out string? dropped);
        }
    }

    private string LastLines()
    {
        string[] lines = _tail.Where(l => l.Contains("[warn]", StringComparison.Ordinal) || l.Contains("[err]", StringComparison.Ordinal)).ToArray();
        if (lines.Length == 0)
        {
            lines = _tail.ToArray();
        }

        return lines.Length == 0 ? "(tor printed nothing)" : "Last output: " + string.Join(" | ", lines[^Math.Min(3, lines.Length)..].Select(Clean));
    }

    /// <summary>A tor log line without its timestamp: "[err] Could not bind …".</summary>
    private static string Clean(string line)
    {
        int bracket = line.IndexOf('[', StringComparison.Ordinal);
        return (bracket >= 0 ? line[bracket..] : line).Trim();
    }

    private static int FreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [GeneratedRegex(@"Bootstrapped (?<pct>\d{1,3})%(?: \([^)]*\))?:?(?<summary>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex BootstrapLine();
}
