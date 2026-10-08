using System.Diagnostics;
using XaultWallet.Core.Monero;

namespace XaultWallet.Core.Tor;

/// <summary>Checks on a tor binary before the app relies on it. Nothing here touches the network.</summary>
public static class TorDiagnostics
{
    /// <summary>Run "&lt;tor&gt; --version" and return its version line ("Tor version 0.4.8.17.").</summary>
    /// <exception cref="InvalidOperationException">It didn't run, or isn't tor.</exception>
    /// <exception cref="TimeoutException">It didn't answer in time.</exception>
    public static async Task<string> ProbeTorAsync(string binaryPath, CancellationToken ct = default)
    {
        ExecutableLocator.EnsureLaunchable(binaryPath); // full paths only, like every launch

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--version");
        TorProcess.AddLibraryPath(psi, binaryPath);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process did not start.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Couldn't run '{binaryPath}'. Check the path is correct and executable. ({ex.Message})", ex);
        }

        using (proc)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);

                string? line = stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => l.StartsWith("Tor version ", StringComparison.Ordinal));
                return line ?? throw new InvalidOperationException("The program ran but didn't report a Tor version. Is this really tor?");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { if (!proc.HasExited) { proc.Kill(true); } } catch { /* ignore */ }
                throw new TimeoutException("tor did not respond to --version in time.");
            }
        }
    }
}
