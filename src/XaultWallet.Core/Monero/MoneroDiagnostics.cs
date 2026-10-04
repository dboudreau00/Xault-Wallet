using System.Diagnostics;
using System.Text.Json;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Lightweight connectivity/health checks used by the Settings screen so the user can
/// verify their monero-wallet-rpc binary and daemon BEFORE trying to create/open a wallet.
/// Neither call touches the vault or any secret.
/// </summary>
public static class MoneroDiagnostics
{
    /// <summary>Run "&lt;binary&gt; --version" and return the reported version line. Throws on failure.</summary>
    public static async Task<string> ProbeWalletRpcAsync(string binaryPath, CancellationToken ct = default)
    {
        ExecutableLocator.EnsureLaunchable(binaryPath); // same rule as the real launch: full paths only

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--version");

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process did not start.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Couldn't run '{binaryPath}'. Check the path is correct and executable. ({ex.Message})", ex);
        }

        using (proc)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                // Read BOTH pipes concurrently: a binary that chatters on stderr could
                // otherwise fill that pipe's buffer and block before exiting.
                Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false); // drained; content not needed

                string? line = stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(line))
                {
                    throw new InvalidOperationException("The program ran but printed no version. Is this really monero-wallet-rpc?");
                }

                if (!line.Contains("Monero", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("wallet-rpc", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unexpected program output: \"{line}\". Is this monero-wallet-rpc?");
                }

                return line;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { if (!proc.HasExited) { proc.Kill(true); } } catch { /* ignore */ }
                throw new TimeoutException("The binary did not respond to --version in time.");
            }
        }
    }

    /// <summary>GET {daemon}/get_height and return the daemon's block height. Throws on failure.
    /// When <paramref name="proxyAddress"/> ("host:port") is set, the probe goes through that
    /// SOCKS5 proxy — the probe MUST take the same route as the wallet backend's traffic, or a
    /// Tor user's real IP would hit the node on every probe while the wallet syncs via Tor.</summary>
    public static async Task<ulong> ProbeDaemonAsync(string daemonAddress, string? proxyAddress, CancellationToken ct = default)
    {
        if (!DaemonAddress.TryParse(daemonAddress, out Uri baseUri))
        {
            throw new ArgumentException("Daemon address must be a valid http(s) URL.", nameof(daemonAddress));
        }

        using HttpClient http = DaemonClient(proxyAddress);
        Uri probeUri = DaemonEndpoint(baseUri, "get_height");

        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(probeUri, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Couldn't reach the daemon at {baseUri}. Is it running? ({ex.Message})", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                throw new InvalidOperationException(
                    $"Daemon returned HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}. " +
                    $"Is monerod running and synced? Response: {(string.IsNullOrWhiteSpace(body) ? "(empty)" : body.Substring(0, Math.Min(200, body.Length)))}");
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("height", out JsonElement h) && h.TryGetUInt64(out ulong height))
            {
                return height;
            }

            throw new InvalidOperationException("The endpoint responded but wasn't a Monero daemon (no height field).");
        }
    }

    /// <summary>
    /// True only for the user's own PRIVATE TEST CHAIN: a node on this machine (loopback) that itself
    /// reports <c>"nettype": "fakechain"</c> — what <c>monerod --regtest</c> runs. Such a chain needs
    /// monero-wallet-rpc's <c>--allow-mismatched-daemon-version</c>: its blocks carry the latest
    /// hard-fork version from height 1, which wallet2's per-height fork check rejects. Never true for a
    /// public network: those nodes report mainnet/stagenet/testnet, and nothing off this machine is
    /// even asked. Any failure (unreachable, not JSON, odd answer) reads as "no".
    /// </summary>
    public static async Task<bool> IsLocalTestChainAsync(string daemonAddress, string? proxyAddress, CancellationToken ct = default)
    {
        if (!DaemonAddress.IsLoopback(daemonAddress) || !DaemonAddress.TryParse(daemonAddress, out Uri baseUri))
        {
            return false;
        }

        try
        {
            using HttpClient http = DaemonClient(proxyAddress, TimeSpan.FromSeconds(5));
            using HttpResponseMessage resp = await http.GetAsync(DaemonEndpoint(baseUri, "get_info"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("nettype", out JsonElement nettype)
                   && nettype.ValueKind == JsonValueKind.String
                   && nettype.GetString() == "fakechain";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// An HTTP client that reaches the node by the SAME route as wallet-rpc: the user's SOCKS proxy
    /// when set, otherwise a DIRECT connection. wallet-rpc ignores system/env proxies, so the probes
    /// must too — or they would reach the node over a different path (and from a different IP).
    /// </summary>
    private static HttpClient DaemonClient(string? proxyAddress, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        if (!string.IsNullOrWhiteSpace(proxyAddress))
        {
            handler.Proxy = new System.Net.WebProxy("socks5://" + proxyAddress.Trim());
            handler.UseProxy = true;
        }

        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    /// <summary>Relative (not rooted), so a node behind a path prefix (e.g. https://host/monero) is
    /// probed at the same URL wallet-rpc will actually use via --daemon-address.</summary>
    private static Uri DaemonEndpoint(Uri baseUri, string path) =>
        baseUri.AbsolutePath.EndsWith('/') ? new Uri(baseUri, path) : new Uri(baseUri + "/" + path);
}
