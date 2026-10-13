using System.Buffers.Binary;

namespace XaultWallet.Core.Companion;

/// <summary>
/// The framing Chrome and Firefox use with a native messaging host: each message is UTF-8 JSON
/// preceded by its length as a 32-bit unsigned integer in the machine's byte order (little-endian on
/// every platform XaultWallet ships for).
/// </summary>
public static class NativeMessaging
{
    /// <summary>Largest message accepted from the browser. The companion's are a few hundred bytes;
    /// the browsers would allow far more, which only a broken or hostile sender needs.</summary>
    public const int MaxIncomingBytes = 64 * 1024;

    /// <summary>Largest message a host may send to the browser (both browsers' limit).</summary>
    public const int MaxOutgoingBytes = 1024 * 1024;

    /// <summary>
    /// The next message, or null when the browser closed the stream between messages (the normal
    /// end). A stream that ends inside a message, or a message over <see cref="MaxIncomingBytes"/>,
    /// throws <see cref="InvalidDataException"/>: the framing can't be trusted after that.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[] header = new byte[4];
        int got = await ReadFullyAsync(input, header, ct).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < header.Length)
        {
            throw new InvalidDataException("The browser's message ended inside its length.");
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxIncomingBytes)
        {
            throw new InvalidDataException($"The browser sent a message of {length} bytes; at most {MaxIncomingBytes} are accepted.");
        }

        byte[] message = new byte[length];
        if (await ReadFullyAsync(input, message, ct).ConfigureAwait(false) < message.Length)
        {
            throw new InvalidDataException("The browser's message ended early.");
        }

        return message;
    }

    /// <summary>Write one message (and flush it: the browser waits for it).</summary>
    public static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (message.Length > MaxOutgoingBytes)
        {
            throw new ArgumentException($"A message to the browser is at most {MaxOutgoingBytes} bytes.", nameof(message));
        }

        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)message.Length);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(message, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadFullyAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await input.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}

/// <summary>
/// The local channel between the native host (or a second launch of the app) and the running app:
/// one request line, one reply line, then the connection closes. A line is a JSON object followed by
/// '\n' (compact JSON never contains a raw newline).
/// </summary>
public static class CompanionWire
{
    /// <summary>One line, without its newline. Null when the other side closed before sending
    /// anything; <see cref="InvalidDataException"/> when the line is longer than
    /// <paramref name="maxBytes"/> or the stream ends inside it.</summary>
    public static async Task<byte[]?> ReadLineAsync(Stream input, int maxBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var line = new MemoryStream();
        byte[] one = new byte[1];
        while (true)
        {
            int n = await input.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (line.Length == 0)
                {
                    return null;
                }

                throw new InvalidDataException("The message ended without a newline.");
            }

            if (one[0] == (byte)'\n')
            {
                return line.ToArray();
            }

            if (line.Length >= maxBytes)
            {
                throw new InvalidDataException($"A message is at most {maxBytes} bytes.");
            }

            line.WriteByte(one[0]);
        }
    }

    public static async Task WriteLineAsync(Stream output, ReadOnlyMemory<byte> json, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (json.Span.IndexOf((byte)'\n') >= 0)
        {
            throw new ArgumentException("A message can't contain a newline.", nameof(json));
        }

        byte[] framed = new byte[json.Length + 1];
        json.CopyTo(framed);
        framed[^1] = (byte)'\n';
        await output.WriteAsync(framed, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
