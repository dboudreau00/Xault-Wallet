using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;

namespace XaultWallet.Core.Monero;

/// <summary>Launch options for the wallet-rpc child.</summary>
public sealed record WalletRpcOptions
{
    /// <summary>SOCKS proxy ("host:port") for the backend's daemon traffic, e.g. Tor at
    /// 127.0.0.1:9050. Null/empty = direct connection.</summary>
    public string? ProxyAddress { get; init; }

    /// <summary>TESTING ONLY. Always pass --allow-mismatched-daemon-version. Not needed for a local
    /// <c>monerod --regtest</c> node: that is detected automatically (see
    /// <see cref="MoneroDiagnostics.IsLocalTestChainAsync"/>). Never enable it against a public network.</summary>
    public bool AllowMismatchedDaemonVersion { get; init; }

    /// <summary>TESTING ONLY. Bind this port instead of a random free one, so a test can occupy it
    /// first and play the process that grabbed the backend's port.</summary>
    internal int? FixedPortForTesting { get; init; }
}

/// <summary>
/// Launches and supervises a monero-wallet-rpc child bound to a random loopback port.
///
/// Everything the child writes lives in ONE private session directory (0700), shredded on stop:
/// <code>
///   xaultwallet_*/wallet/          restored wallet files (--wallet-dir)
///   xaultwallet_*/ringdb/          shared ring database (--shared-ringdb-dir; default is ~/.shared-ringdb)
///   xaultwallet_*/wallet-rpc.log   wallet-rpc's own log  (--log-file; default is the process CWD)
///   xaultwallet_*/rpc.conf         per-session RPC credentials (0600; shredded once the server is up)
/// </code>
/// The RPC server requires HTTP Digest auth with those random credentials, so a web page (CSRF or
/// DNS rebinding) or another local process cannot drive the open wallet. Before any secret is sent,
/// the listening socket is checked to belong to the child (where the OS allows it).
/// </summary>
public sealed class MoneroProcessManager : IAsyncDisposable
{
    private readonly string _walletRpcBinary;
    private readonly WalletRpcOptions _options;
    private Process? _process;
    private int _port;
    private string? _tempDir;
    private ManualResetEventSlim? _parentThreadRelease; // see StartChildAsync
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private const int StderrTailMax = 60;

    /// <summary>The wallet's file name inside the session's --wallet-dir.</summary>
    internal const string WalletFileName = "w";

    public Uri? Endpoint { get; private set; }

    /// <summary>True when the node is the user's own private test chain (a local regtest node), which
    /// the UI labels as such instead of as a real network.</summary>
    public bool IsLocalTestChain { get; private set; }

    /// <summary>The live session directory (tests inspect what the child writes there).</summary>
    internal string? SessionDirectory => _tempDir;

    /// <summary>The child's PID while it runs (tests inspect its command line).</summary>
    internal int? ProcessId => _process?.Id;

    public MoneroProcessManager(string walletRpcBinary, string? proxyAddress = null)
        : this(walletRpcBinary, new WalletRpcOptions { ProxyAddress = proxyAddress })
    {
    }

    public MoneroProcessManager(string walletRpcBinary, WalletRpcOptions options)
    {
        _walletRpcBinary = walletRpcBinary ?? throw new ArgumentNullException(nameof(walletRpcBinary));
        ArgumentNullException.ThrowIfNull(options);
        _options = options with
        {
            ProxyAddress = string.IsNullOrWhiteSpace(options.ProxyAddress) ? null : options.ProxyAddress.Trim(),
        };
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

    private static string NetworkFlag(MoneroNetwork n) => n switch
    {
        MoneroNetwork.Stagenet => "--stagenet",
        MoneroNetwork.Testnet => "--testnet",
        _ => string.Empty,
    };

    /// <summary>
    /// Restore a wallet into a fresh session directory: from its seed, or from its keys (full or
    /// view-only), per <see cref="WalletSecrets.Kind"/>. Starts monero-wallet-rpc with no wallet open
    /// (so startup never blocks on the daemon), then restores via restore_deterministic_wallet or
    /// generate_from_keys. The wallet syncs in the background afterward.
    /// </summary>
    public Task<MoneroRpcClient> StartFromSeedAsync(WalletSecrets secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (secrets.Kind == WalletKind.Seed && string.IsNullOrWhiteSpace(secrets.Mnemonic))
        {
            throw new ArgumentException("WalletSecrets.Mnemonic is empty.", nameof(secrets));
        }

        if (secrets.Kind != WalletKind.Seed
            && (string.IsNullOrWhiteSpace(secrets.Address) || string.IsNullOrWhiteSpace(secrets.ViewKey)
                || (secrets.Kind == WalletKind.Keys && string.IsNullOrWhiteSpace(secrets.SpendKey))))
        {
            throw new ArgumentException("The wallet's address or keys are missing.", nameof(secrets));
        }

        return StartFromSeedInternalAsync(secrets, ct);
    }

    private async Task<MoneroRpcClient> StartFromSeedInternalAsync(WalletSecrets secrets, CancellationToken ct)
    {
        MoneroRpcClient client = await LaunchAsync(secrets.Network, secrets.DaemonAddress, ct).ConfigureAwait(false);

        try
        {
            EnsureBackendIsOurs(); // last check before the seed or keys go over the wire
            if (secrets.Kind == WalletKind.Seed)
            {
                await client.RestoreDeterministicWalletAsync(
                    filename: WalletFileName,
                    password: secrets.EphemeralWalletPassword,
                    seed: secrets.Mnemonic.Trim(),
                    restoreHeight: secrets.RestoreHeight,
                    seedOffset: secrets.SeedOffset ?? string.Empty,
                    ct).ConfigureAwait(false);
            }
            else
            {
                await client.GenerateFromKeysAsync(
                    filename: WalletFileName,
                    password: secrets.EphemeralWalletPassword,
                    address: secrets.Address.Trim(),
                    viewKey: secrets.ViewKey.Trim(),
                    spendKey: secrets.Kind == WalletKind.Keys ? secrets.SpendKey.Trim() : string.Empty,
                    restoreHeight: secrets.RestoreHeight,
                    ct).ConfigureAwait(false);
            }

            // Deliberately no node, height or kind here: the log persists, and per-wallet details
            // (a restore height is unique to a seed) would let it tell two wallets apart.
        }
        catch
        {
            client.Dispose();
            await StopAsync().ConfigureAwait(false); // don't leak the process/session dir on failure
            throw;
        }

        return client;
    }

    /// <summary>
    /// Start monero-wallet-rpc with NO wallet open, ready to accept create_wallet / open_wallet.
    /// Used by the seed-generation flow.
    /// </summary>
    public Task<MoneroRpcClient> StartServerAsync(MoneroNetwork network, string daemonAddress, CancellationToken ct = default) =>
        LaunchAsync(network, daemonAddress, ct);

    private async Task<MoneroRpcClient> LaunchAsync(MoneroNetwork network, string daemonAddress, CancellationToken ct)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("A wallet process is already running for this manager.");
        }

        ExecutableLocator.EnsureLaunchable(_walletRpcBinary);
        XaultWallet.Core.Installer.WalletRpcInstaller.EnsureMatchesManifest(_walletRpcBinary);

        if (!DaemonAddress.IsValid(daemonAddress))
        {
            throw new ArgumentException("Daemon address is not a valid http(s) URL.", nameof(daemonAddress));
        }

        // Decided per launch from what the node itself reports — only ever true for a node on this
        // machine that says it is a regtest chain (never for mainnet, stagenet or testnet).
        IsLocalTestChain = await MoneroDiagnostics.IsLocalTestChainAsync(daemonAddress, _options.ProxyAddress, ct).ConfigureAwait(false);
        bool allowMismatchedDaemon = _options.AllowMismatchedDaemonVersion || IsLocalTestChain;

        _tempDir = CreatePrivateDirectory();
        string walletDir = Directory.CreateDirectory(Path.Combine(_tempDir, "wallet")).FullName;
        string ringDbDir = Directory.CreateDirectory(Path.Combine(_tempDir, "ringdb")).FullName;
        string configFile = Path.Combine(_tempDir, "rpc.conf");

        // Per-session random credentials, passed via a private config file rather than argv:
        // other local users can read a process's command line (/proc/<pid>/cmdline).
        var credential = new NetworkCredential(RandomHex(8), RandomHex(32));

        MoneroRpcClient? client = null;
        try
        {
            WritePrivateFile(configFile, $"rpc-login={credential.UserName}:{credential.Password}\n");

            int port = _options.FixedPortForTesting ?? FreeLocalPort();
            _port = port;
            Endpoint = new Uri($"http://127.0.0.1:{port}");

            var psi = new ProcessStartInfo
            {
                FileName = _walletRpcBinary,
                WorkingDirectory = _tempDir, // anything written relative to CWD lands in the shredded dir
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            void Arg(string name, string? value = null)
            {
                psi.ArgumentList.Add(name);
                if (value is not null)
                {
                    psi.ArgumentList.Add(value);
                }
            }

            Arg("--rpc-bind-ip", "127.0.0.1");
            Arg("--rpc-bind-port", port.ToString(CultureInfo.InvariantCulture));
            Arg("--config-file", configFile);
            Arg("--wallet-dir", walletDir);
            Arg("--daemon-address", daemonAddress.Trim());
            if (DaemonSslMode(daemonAddress) == "enabled")
            {
                // An https:// node must really be reached over TLS, never plain HTTP: see DaemonSslMode.
                Arg("--daemon-ssl", "enabled");
                Arg("--daemon-ssl-allow-any-cert");
            }

            if (_options.ProxyAddress is not null)
            {
                // Route daemon traffic through the user's SOCKS proxy (e.g. Tor at 127.0.0.1:9050)
                // so the configured node never learns the user's real IP.
                Arg("--proxy", _options.ProxyAddress);
            }

            Arg("--log-file", Path.Combine(_tempDir, "wallet-rpc.log"));
            Arg("--log-level", "0");
            Arg("--shared-ringdb-dir", ringDbDir);
            string netFlag = NetworkFlag(network);
            if (netFlag.Length > 0)
            {
                Arg(netFlag);
            }

            if (allowMismatchedDaemon)
            {
                Arg("--allow-mismatched-daemon-version");
            }

            _process = await StartChildAsync(psi).ConfigureAwait(false);

            // Record who owns this folder, so a later startup sweep can tell a live second instance
            // from a crash orphan (and stop an orphaned wallet-rpc) instead of guessing from mtimes.
            WritePrivateFile(Path.Combine(_tempDir, SessionHygiene.PidFileName),
                $"app={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} child={_process.Id.ToString(CultureInfo.InvariantCulture)}\n");

            // Tie the child to OUR lifetime: if XaultWallet crashes or is killed, the OS closes
            // the job handle and takes wallet-rpc (and the open wallet) down with it.
            WindowsChildJob.TryAssign(_process);

            // Enhanced Security (Windows): its memory, which holds the open wallet, is closed to other
            // programs too, now if it is on or as soon as it is turned on.
            XaultWallet.Core.Security.ProcessHardening.Track(_process);

            // CRITICAL: drain both pipes, or a chatty child fills the pipe buffer and blocks.
            _process.OutputDataReceived += (_, _) => { /* discard stdout */ };
            // NOTE: the sender parameter must NOT be named "_": a single lambda parameter
            // named "_" is a real variable of type object, which hijacks the "out _"
            // discard below and breaks compilation (CS1503).
            _process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                _stderrTail.Enqueue(e.Data);
                while (_stderrTail.Count > StderrTailMax)
                {
                    _stderrTail.TryDequeue(out _);
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            client = new MoneroRpcClient(Endpoint, credential);
            await WaitUntilReadyAsync(client, port, ct).ConfigureAwait(false);

            // The server parsed its config at startup; the credentials now live only in memory.
            SecureDelete.File(configFile);

            return client;
        }
        catch (Exception ex)
        {
            Log.ErrorOnce("wallet-rpc-launch", "monero-wallet-rpc launch failed", ex);
            client?.Dispose(); // don't leak the HttpClient/handler on a failed launch
            await StopAsync().ConfigureAwait(false); // no leaked process / session dir
            throw;
        }
    }

    /// <summary>
    /// Start the child. On Linux with a working <c>setpriv</c> it is started as
    /// <c>setpriv --pdeathsig SIGKILL -- monero-wallet-rpc …</c>, so the kernel kills it when this
    /// process dies however it dies (crash, SIGKILL): no unlocked wallet-rpc is left running. The
    /// kernel ties that signal to the THREAD that forked the child, and thread-pool threads come and
    /// go, so the child is started from a dedicated thread that stays alive until <see cref="StopAsync"/>.
    /// Without setpriv (or off Linux) it is the plain start, as before.
    /// </summary>
    private async Task<Process> StartChildAsync(ProcessStartInfo psi)
    {
        string? setpriv = SessionHygiene.SetprivPath;
        if (setpriv is null)
        {
            return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start monero-wallet-rpc.");
        }

        string binary = psi.FileName;
        psi.FileName = setpriv;
        psi.ArgumentList.Insert(0, binary);
        psi.ArgumentList.Insert(0, "--");
        psi.ArgumentList.Insert(0, "SIGKILL");
        psi.ArgumentList.Insert(0, "--pdeathsig");

        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        _parentThreadRelease = release;
        var parent = new Thread(() =>
        {
            try
            {
                started.SetResult(Process.Start(psi) ?? throw new InvalidOperationException("Failed to start monero-wallet-rpc."));
            }
            catch (Exception ex)
            {
                started.SetException(ex);
                return;
            }

            release.Wait(); // keep the forking thread (and so the death signal's anchor) alive
        })
        {
            IsBackground = true,
            Name = "wallet-rpc-parent",
        };
        parent.Start();
        return await started.Task.ConfigureAwait(false);
    }

    /// <summary>True when the child was started but has since died (crash, external kill).
    /// Lets callers distinguish "backend is gone" from "node is slow".</summary>
    public bool HasProcessExited
    {
        get
        {
            try
            {
                return _process is { HasExited: true };
            }
            catch (InvalidOperationException)
            {
                return false; // process handle no longer queryable
            }
        }
    }

    /// <summary>
    /// Ready == our child answers an authenticated request. get_version works with or without an
    /// open wallet, and any RPC-level error also proves the server is up — except a 401, which
    /// means the session credentials were refused. Only connection-level failures mean "not yet".
    /// Before declaring ready, verify the listening socket belongs to the child: nothing
    /// secret-bearing may go to a port some other process grabbed first.
    /// </summary>
    private async Task WaitUntilReadyAsync(MoneroRpcClient client, int port, CancellationToken ct)
    {
        const int maxAttempts = 120; // ~60s at 500ms
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"monero-wallet-rpc exited early (code {_process.ExitCode}). {LastStderr()}");
            }

            bool answered;
            try
            {
                await client.GetVersionAsync(ct).ConfigureAwait(false);
                answered = true;
            }
            catch (MoneroRpcClient.MoneroRpcException ex) when (ex.Code == 401)
            {
                throw new InvalidOperationException(
                    "The wallet backend refused this session's credentials. Is another program using its port?");
            }
            catch (MoneroRpcClient.MoneroRpcException)
            {
                answered = true; // server responded with an error => it is up
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                answered = false; // connection not up yet
            }

            if (answered)
            {
                EnsureBackendIsOurs();
                return;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"monero-wallet-rpc did not become ready in time. {LastStderr()}");
    }

    /// <summary>
    /// wallet-rpc's <c>--daemon-ssl</c> (and <c>set_daemon</c>'s <c>ssl_support</c>) for a node
    /// address: <c>"enabled"</c> for an <c>https://</c> node, otherwise <c>"autodetect"</c>, which is
    /// wallet-rpc's own default (so <c>http://</c> nodes behave exactly as before).
    /// Why: under "autodetect", when the TLS handshake fails wallet-rpc reconnects without TLS, so
    /// anyone on the path can strip the encryption the user asked for by typing https. Under "enabled"
    /// it never falls back to plain HTTP.
    /// What monero-wallet-rpc requires (v0.18.4.x, wallet2.cpp make_basic and on_set_daemon; epee
    /// net_ssl.cpp has_strong_verification): "enabled" with the default system-CA verification is
    /// refused when a wallet is opened or created ("Enabling --daemon-ssl requires
    /// --daemon-ssl-allow-any-cert or --daemon-ssl-ca-certificates or --daemon-ssl-allowed-fingerprints
    /// or use of a .onion/.i2p domain"). A CA file is checked without the host name (and, without
    /// --daemon-ssl-allow-chained, only as the exact leaf), so the system bundle is no substitute for
    /// real verification. So an https node also gets <c>--daemon-ssl-allow-any-cert</c>
    /// (<see cref="DaemonSslAllowAnyCert"/>): the traffic is always encrypted, but the certificate is
    /// not checked, exactly as under "autodetect" (which only logs a warning for an unverified one).
    /// Authenticating the node would need a certificate fingerprint pin (a future setting).
    /// </summary>
    internal static string DaemonSslMode(string daemonAddress) =>
        Uri.TryCreate(daemonAddress.Trim(), UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
            ? "enabled"
            : "autodetect";

    /// <summary>wallet-rpc's <c>--daemon-ssl-allow-any-cert</c> (<c>set_daemon</c>'s
    /// <c>ssl_allow_any_cert</c>): true exactly when <see cref="DaemonSslMode"/> is "enabled", which
    /// wallet-rpc otherwise refuses (see there).</summary>
    internal static bool DaemonSslAllowAnyCert(string daemonAddress) => DaemonSslMode(daemonAddress) == "enabled";

    /// <summary>
    /// Throw unless the server on our port is provably the child we started and that child is still
    /// alive. Called when the server first answers AND right before the first call that carries a
    /// secret. Fails closed: only a definite "yes" passes. Where ownership cannot be checked
    /// (macOS) the call always throws. A child that already died after losing the port to another
    /// process also reads as "no".
    /// </summary>
    public void EnsureBackendIsOurs()
    {
        Process? p = _process;
        if (p is null || HasProcessExited)
        {
            throw new InvalidOperationException(
                $"monero-wallet-rpc is not running (it may have failed to start). {LastStderr()}");
        }

        if (!LoopbackPortOwnership.IsSupported)
        {
            // macOS (and anything else without a port-owner check): refuse seed-bearing RPC rather
            // than trust digest auth alone. Digest authenticates the client, not the server.
            throw new InvalidOperationException(
                "XaultWallet can't yet confirm on this system (macOS) that the wallet backend's port " +
                "belongs to it, so it refuses to send your wallet to it. Use Linux or Windows for now, " +
                "or wait for a release that implements the macOS ownership check.");
        }

        if (!LoopbackPortOwnership.IsTrusted(LoopbackPortOwnership.IsListenerOwnedBy(p.Id, _port)) || HasProcessExited)
        {
            throw new InvalidOperationException(
                "Another program is answering on the wallet backend's port, so XaultWallet refused to send it " +
                "your wallet. Close other wallet software and try again.");
        }
    }

    // Base58 runs of address length and 64-hex strings (keys, tx ids) are masked from anything the
    // stderr tail feeds into: exception messages reach the screen and the persistent log.
    private static readonly Regex AddressLike = new("[1-9A-HJ-NP-Za-km-z]{90,}", RegexOptions.Compiled);
    private static readonly Regex HexBlobLike = new("[0-9a-fA-F]{64,}", RegexOptions.Compiled);

    internal static string ScrubLine(string line) =>
        HexBlobLike.Replace(AddressLike.Replace(line, "***"), "***");

    private string LastStderr()
    {
        string[] lines = _stderrTail.ToArray();
        if (lines.Length == 0)
        {
            return "(no stderr captured)";
        }

        return "Last output: " + string.Join(" | ", lines[^Math.Min(4, lines.Length)..].Select(ScrubLine));
    }

    public async Task StopAsync()
    {
        Process? p = _process;
        _process = null;

        if (p is not null)
        {
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
                Log.WarnOnce("wallet-rpc-stop", $"Error stopping monero-wallet-rpc: {ex.GetType().Name}");
            }
            finally
            {
                p.Dispose();
            }
        }

        _parentThreadRelease?.Set(); // the child is gone: let its forking thread end
        _parentThreadRelease = null;

        string? dir = _tempDir;
        _tempDir = null;
        if (dir is not null && Directory.Exists(dir) && !SecureDelete.Directory(dir))
        {
            // The shred is a privacy guarantee; if it couldn't complete (e.g. a wedged child still
            // holds file locks), at least say so instead of failing silently.
            Log.WarnOnce("session-shred", "Some temporary wallet files could not be removed; they will be re-shredded on next launch if still present.");
        }
    }

    /// <summary>
    /// Shred any xaultwallet_* session directories left over from a previous session that crashed
    /// or was killed before its own cleanup ran. Call ONCE at startup, before any wallet is opened,
    /// and only when no other instance is running (the app's single-instance guard ensures this).
    /// </summary>
    public static void ShredOrphanedTempDirs()
    {
        try
        {
            // Never follow links or enter folders that aren't ours: the shared temp folder is writable
            // by every local user, and what is found here gets overwritten and deleted.
            var listing = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (string root in SessionHygiene.SweepRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string dir in Directory.EnumerateDirectories(root, "xaultwallet_*", listing))
                {
                    if (!SessionHygiene.IsSafeSessionDir(dir))
                    {
                        continue; // a link, someone else's folder, or open to others: not ours to shred
                    }

                    // A folder that records its owner is judged by liveness: the app that made it is
                    // running (leave it) or gone (shred it, after stopping a wallet-rpc left behind).
                    // Only a folder without that record (an older build) falls back to the age guard.
                    SessionHygiene.SessionState state = SessionHygiene.Inspect(dir);
                    if (state == SessionHygiene.SessionState.OwnerAlive)
                    {
                        continue;
                    }

                    if (state == SessionHygiene.SessionState.Unknown && !IsQuietFor(dir, TimeSpan.FromMinutes(15), listing))
                    {
                        continue;
                    }

                    Log.WarnOnce("orphan-sweep", "Shredding orphaned wallet session folders from a previous session."); // not one line per wallet
                    SecureDelete.Directory(dir);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Orphaned temp dir sweep failed: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Age guard for folders with no PID record: an instance of an OLDER build (which predates the
    /// single-instance mutex) could still be running with a LIVE wallet in one of these. A live dir is
    /// written constantly while syncing; a crash orphan goes quiet. False when anything in it was written
    /// within <paramref name="quiet"/>, or it can't be read (likely in use).
    /// </summary>
    private static bool IsQuietFor(string dir, TimeSpan quiet, EnumerationOptions listing)
    {
        DateTime newestWriteUtc = Directory.GetLastWriteTimeUtc(dir);
        try
        {
            var all = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = listing.AttributesToSkip, IgnoreInaccessible = true };
            foreach (string file in Directory.EnumerateFiles(dir, "*", all))
            {
                DateTime w = File.GetLastWriteTimeUtc(file);
                if (w > newestWriteUtc)
                {
                    newestWriteUtc = w;
                }
            }
        }
        catch
        {
            return false;
        }

        return DateTime.UtcNow - newestWriteUtc >= quiet;
    }

    /// <summary>A fresh xaultwallet_* directory readable by this user only.</summary>
    private static string CreatePrivateDirectory()
    {
        // The per-user runtime folder when there is one (Linux: private to the user, and the wallet's
        // files stay in memory); else the temp folder.
        string root = SessionHygiene.SessionRoot();
        string dir;
        if (OperatingSystem.IsWindows() || string.Equals(root, Path.GetTempPath(), StringComparison.Ordinal))
        {
            dir = Directory.CreateTempSubdirectory("xaultwallet_").FullName;
        }
        else
        {
            dir = Directory.CreateDirectory(
                Path.Combine(root, "xaultwallet_" + RandomHex(6)),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
        }

        if (!OperatingSystem.IsWindows())
        {
            // CreateTempSubdirectory already uses 0700 on Unix; state the requirement explicitly.
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }

    private static void WritePrivateFile(string path, string contents)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var fs = new FileStream(path, options);
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(contents);
        try
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string RandomHex(int bytes) =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
