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
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            throw new ArgumentException("No monero-wallet-rpc path is set.", nameof(binaryPath));
        }

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

    /// <summary>GET {daemon}/get_height and return the daemon's block height. Throws on failure.</summary>
    public static async Task<ulong> ProbeDaemonAsync(string daemonAddress, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(daemonAddress?.Trim(), UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Daemon address must be a valid http(s) URL.", nameof(daemonAddress));
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(new Uri(baseUri, "/get_height"), ct).ConfigureAwait(false);
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
}
