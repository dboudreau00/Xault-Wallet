using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace XaultWallet.E2E;

/// <summary>
/// Talks to XaultWallet's native messaging host the way Chrome does: starts it with the extension's
/// origin as its argument, writes one length-prefixed JSON message to its standard input, reads one
/// reply from its standard output, then closes the input (which ends the host).
/// </summary>
public static class NativeHostClient
{
    /// <summary>The origin Chrome passes for the companion extension.</summary>
    public const string ChromeOrigin = "chrome-extension://kpcmnhbbahdifkhgibnboafelgfkgcdj/";

    public static async Task<string> AskAsync(ProcessStartInfo start, string json, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;

        using Process host = Process.Start(start) ?? throw new InvalidOperationException("The native host didn't start.");
        Task<string> stderr = host.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
            Stream input = host.StandardInput.BaseStream;
            await input.WriteAsync(header, cts.Token);
            await input.WriteAsync(body, cts.Token);
            await input.FlushAsync(cts.Token);

            Stream output = host.StandardOutput.BaseStream;
            byte[] lengthBytes = await ReadExactlyAsync(output, 4, cts.Token);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            if (length is <= 0 or > 1024 * 1024)
            {
                throw new InvalidDataException($"The native host replied with a length of {length}.");
            }

            string reply = Encoding.UTF8.GetString(await ReadExactlyAsync(output, length, cts.Token));
            host.StandardInput.Close(); // the browser closing the port: the host must end
            await host.WaitForExitAsync(cts.Token);
            if (host.ExitCode != 0)
            {
                throw new InvalidOperationException($"The native host exited with {host.ExitCode}: {await stderr}");
            }

            return reply;
        }
        catch (OperationCanceledException)
        {
            try { host.Kill(); } catch (InvalidOperationException) { /* gone */ }
            throw new TimeoutException($"The native host didn't answer within {timeout.TotalSeconds:0}s. {await stderr}");
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                throw new InvalidDataException($"The native host closed its output after {read} of {count} bytes.");
            }

            read += n;
        }

        return buffer;
    }
}
