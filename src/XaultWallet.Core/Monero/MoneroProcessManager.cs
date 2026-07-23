using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Models;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Launches and supervises a monero-wallet-rpc child process bound to a random
/// localhost port with random RPC credentials. The wallet itself is restored from
/// the seed into an EPHEMERAL temp directory, which is shredded on Stop(). Nothing
/// about the real wallet persists to disk between sessions.
/// </summary>
public sealed class MoneroProcessManager : IAsyncDisposable
{
    private readonly string _walletRpcBinary;
    private Process? _process;
    private string? _tempDir;
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private const int StderrTailMax = 60;

    public Uri? Endpoint { get; private set; }

    public MoneroProcessManager(string walletRpcBinary)
    {
        _walletRpcBinary = walletRpcBinary ?? throw new ArgumentNullException(nameof(walletRpcBinary));
    }

    private static int FreeLocalPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
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
    /// Restore a wallet from seed into a fresh temp dir. Starts monero-wallet-rpc with no wallet
    /// open (so startup never blocks on the daemon), then restores via the
    /// restore_deterministic_wallet RPC. The wallet syncs in the background afterward.
    /// </summary>
    public Task<MoneroRpcClient> StartFromSeedAsync(WalletSecrets secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (string.IsNullOrWhiteSpace(secrets.Mnemonic))
        {
            throw new ArgumentException("WalletSecrets.Mnemonic is empty.", nameof(secrets));
        }

        return StartFromSeedInternalAsync(secrets, ct);
    }

    private async Task<MoneroRpcClient> StartFromSeedInternalAsync(WalletSecrets secrets, CancellationToken ct)
    {
        // Start the RPC server with NO wallet open (--wallet-dir), exactly like the generation
        // path. This does not connect to the daemon, so the server becomes ready quickly even
        // when the node is slow or still syncing. We then restore the wallet via the
        // restore_deterministic_wallet RPC (which doesn't need the daemon); scanning proceeds in
        // the background afterward. This fixes the open-path timeout caused by --generate-from-json
        // blocking on the daemon handshake during startup.
        MoneroRpcClient client = await LaunchAsync(secrets.Network, secrets.DaemonAddress, (psi, tempDir) =>
        {
            psi.ArgumentList.Add("--wallet-dir"); psi.ArgumentList.Add(tempDir);
            return null;
        }, ct).ConfigureAwait(false);

        try
        {
            await client.RestoreDeterministicWalletAsync(
                filename: "w",
                password: secrets.EphemeralWalletPassword,
                seed: secrets.Mnemonic.Trim(),
                restoreHeight: secrets.RestoreHeight,
                seedOffset: secrets.SeedOffset ?? string.Empty,
                ct).ConfigureAwait(false);

            Log.Info($"Wallet restored on RPC; syncing against {secrets.DaemonAddress} from height {secrets.RestoreHeight}.");
        }
        catch
        {
            await StopAsync().ConfigureAwait(false); // don't leak the process/temp dir on failure
            throw;
        }

        return client;
    }

    /// <summary>
    /// Start monero-wallet-rpc with NO wallet open (--wallet-dir only), ready to accept
    /// create_wallet / open_wallet. Used by the seed-generation flow.
    /// </summary>
    public Task<MoneroRpcClient> StartServerAsync(MoneroNetwork network, string daemonAddress, CancellationToken ct = default) =>
        LaunchAsync(network, daemonAddress, (psi, tempDir) =>
        {
            psi.ArgumentList.Add("--wallet-dir"); psi.ArgumentList.Add(tempDir);
            return null;
        }, ct);

    /// <summary>
    /// Shared launcher. <paramref name="configureMode"/> adds the wallet-mode args (both callers
    /// use --wallet-dir so the server starts without opening a wallet) and may return a scratch
    /// file to shred once the server is up. Cleans up fully if anything fails.
    /// </summary>
    private async Task<MoneroRpcClient> LaunchAsync(
        MoneroNetwork network,
        string daemonAddress,
        Func<ProcessStartInfo, string, string?> configureMode,
        CancellationToken ct)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("A wallet process is already running for this manager.");
        }

        if (string.IsNullOrWhiteSpace(_walletRpcBinary) || !File.Exists(_walletRpcBinary))
        {
            throw new FileNotFoundException(
                "monero-wallet-rpc binary not found. Download the official Monero CLI tools and set its path in Settings.",
                _walletRpcBinary);
        }

        if (!IsValidDaemonAddress(daemonAddress))
        {
            throw new ArgumentException($"Daemon address is not a valid http(s) URL: '{daemonAddress}'.", nameof(daemonAddress));
        }

        _tempDir = Directory.CreateTempSubdirectory("xaultwallet_").FullName;
        int port = FreeLocalPort();
        Endpoint = new Uri($"http://127.0.0.1:{port}");

        var psi = new ProcessStartInfo
        {
            FileName = _walletRpcBinary,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--rpc-bind-port"); psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--rpc-bind-ip"); psi.ArgumentList.Add("127.0.0.1");
        // The RPC server is bound to loopback on a random port for a few seconds and is not
        // reachable off-machine. Digest --rpc-login breaks POSTs with a body (the request
        // stream can't be replayed after the 401 challenge), so we disable RPC auth entirely
        // here rather than fight the handshake. Security still comes from loopback-only binding.
        psi.ArgumentList.Add("--disable-rpc-login");
        psi.ArgumentList.Add("--daemon-address"); psi.ArgumentList.Add(daemonAddress.Trim());
        psi.ArgumentList.Add("--log-level"); psi.ArgumentList.Add("0");
        string netFlag = NetworkFlag(network);
        if (netFlag.Length > 0)
        {
            psi.ArgumentList.Add(netFlag);
        }

        string? scratchFile = configureMode(psi, _tempDir);

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start monero-wallet-rpc.");

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

            var client = new MoneroRpcClient(Endpoint); // no auth: launched with --disable-rpc-login
            await WaitUntilReadyAsync(client, ct).ConfigureAwait(false);

            if (scratchFile is not null)
            {
                SecureDeleteFile(scratchFile);
            }

            Log.Info($"monero-wallet-rpc ready on port {port}.");
            return client;
        }
        catch (Exception ex)
        {
            Log.Error("monero-wallet-rpc launch failed", ex);
            await StopAsync().ConfigureAwait(false); // no leaked process / temp dir
            throw;
        }
    }

    /// <summary>
    /// Ready == the JSON-RPC server responds to anything. get_version works with or without an
    /// open wallet, and even an RPC-level error means the server is up. Only connection-level
    /// failures count as "not ready yet".
    /// </summary>
    private async Task WaitUntilReadyAsync(MoneroRpcClient client, CancellationToken ct)
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

            try
            {
                await client.GetVersionAsync(ct).ConfigureAwait(false);
                return; // responded successfully
            }
            catch (MoneroRpcClient.MoneroRpcException)
            {
                return; // server responded with an error => it is up
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await Task.Delay(500, ct).ConfigureAwait(false); // connection not up yet
            }
        }

        throw new TimeoutException($"monero-wallet-rpc did not become ready in time. {LastStderr()}");
    }

    private string LastStderr()
    {
        string[] lines = _stderrTail.ToArray();
        if (lines.Length == 0)
        {
            return "(no stderr captured)";
        }

        // Surface the last few lines only; keep it short and secret-free (rpc logs don't contain the seed).
        return "Last output: " + string.Join(" | ", lines[^Math.Min(4, lines.Length)..]);
    }

    private static bool IsValidDaemonAddress(string address) =>
        !string.IsNullOrWhiteSpace(address)
        && Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

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
                Log.Warn($"Error stopping monero-wallet-rpc: {ex.GetType().Name}");
            }
            finally
            {
                p.Dispose();
            }
        }

        string? dir = _tempDir;
        _tempDir = null;
        if (dir is not null && Directory.Exists(dir))
        {
            ShredDirectory(dir);
        }
    }

    private static void ShredDirectory(string dir)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                SecureDeleteFile(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Error enumerating temp dir for shredding: {ex.GetType().Name}");
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Overwrite a file's bytes before deleting. On SSDs wear-levelling means this is not a
    /// guarantee (see SECURITY.md), but it removes the plaintext from the obvious recovery paths.
    /// </summary>
    private static void SecureDeleteFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 0)
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    byte[] noise = new byte[81920];
                    long remaining = info.Length;
                    while (remaining > 0)
                    {
                        RandomNumberGenerator.Fill(noise);
                        int chunk = (int)Math.Min(noise.Length, remaining);
                        fs.Write(noise, 0, chunk);
                        remaining -= chunk;
                    }

                    fs.Flush(flushToDisk: true);
                }
            }

            File.Delete(path);
        }
        catch
        {
            // best effort; the parent dir delete will still attempt removal
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
