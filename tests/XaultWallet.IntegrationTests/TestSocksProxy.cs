using System.Net;
using System.Net.Sockets;

namespace XaultWallet.IntegrationTests;

/// <summary>
/// A SOCKS proxy on this computer that connects wherever it is asked and counts who asked, standing in for
/// Tor: monero-wallet-rpc speaks SOCKS4/4a to its --proxy, the app's own node probes speak SOCKS5, and Tor
/// takes both on one port. No authentication, CONNECT only.
/// </summary>
internal sealed class TestSocksProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _socks4;
    private int _socks5;

    public TestSocksProxy()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    /// <summary>host:port, as the app and wallet-rpc are given a proxy.</summary>
    public string Address => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Connections made the way monero-wallet-rpc makes them.</summary>
    public int Socks4Connections => Volatile.Read(ref _socks4);

    /// <summary>Connections made the way the app's own HTTP probes make them.</summary>
    public int Socks5Connections => Volatile.Read(ref _socks5);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // stopped
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                byte[] version = new byte[1];
                await stream.ReadExactlyAsync(version, _stop.Token);
                (string Host, int Port) target = version[0] switch
                {
                    4 => await ReadSocks4RequestAsync(stream),
                    5 => await ReadSocks5RequestAsync(stream),
                    _ => throw new InvalidDataException("not SOCKS"),
                };

                using var upstream = new TcpClient();
                await upstream.ConnectAsync(target.Host, target.Port, _stop.Token);
                if (version[0] == 4)
                {
                    Interlocked.Increment(ref _socks4);
                    await stream.WriteAsync(new byte[] { 0, 0x5A, 0, 0, 0, 0, 0, 0 }, _stop.Token);
                }
                else
                {
                    Interlocked.Increment(ref _socks5);
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token);
                }

                NetworkStream other = upstream.GetStream();
                await Task.WhenAny(stream.CopyToAsync(other, _stop.Token), other.CopyToAsync(stream, _stop.Token));
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                // one connection failing is that connection's problem
            }
        }
    }

    /// <summary>After the version byte: command, port, IPv4 address, user id, and for SOCKS4a (an address of
    /// 0.0.0.x) the host name.</summary>
    private async Task<(string, int)> ReadSocks4RequestAsync(NetworkStream stream)
    {
        byte[] head = new byte[7];
        await stream.ReadExactlyAsync(head, _stop.Token);
        if (head[0] != 1)
        {
            throw new InvalidDataException("not CONNECT");
        }

        int port = (head[1] << 8) | head[2];
        var address = new IPAddress(head.AsSpan(3, 4));
        await ReadUntilNulAsync(stream); // the user id
        bool byName = head[3] == 0 && head[4] == 0 && head[5] == 0 && head[6] != 0;
        return (byName ? await ReadUntilNulAsync(stream) : address.ToString(), port);
    }

    /// <summary>After the version byte: the methods offered (answered with "no authentication"), then the request.</summary>
    private async Task<(string, int)> ReadSocks5RequestAsync(NetworkStream stream)
    {
        byte[] count = new byte[1];
        await stream.ReadExactlyAsync(count, _stop.Token);
        await stream.ReadExactlyAsync(new byte[count[0]], _stop.Token);
        await stream.WriteAsync(new byte[] { 5, 0 }, _stop.Token);

        byte[] request = new byte[4];
        await stream.ReadExactlyAsync(request, _stop.Token);
        if (request[0] != 5 || request[1] != 1)
        {
            throw new InvalidDataException("not CONNECT");
        }

        string host;
        switch (request[3])
        {
            case 1:
                byte[] v4 = new byte[4];
                await stream.ReadExactlyAsync(v4, _stop.Token);
                host = new IPAddress(v4).ToString();
                break;
            case 3:
                byte[] length = new byte[1];
                await stream.ReadExactlyAsync(length, _stop.Token);
                byte[] name = new byte[length[0]];
                await stream.ReadExactlyAsync(name, _stop.Token);
                host = System.Text.Encoding.ASCII.GetString(name);
                break;
            case 4:
                byte[] v6 = new byte[16];
                await stream.ReadExactlyAsync(v6, _stop.Token);
                host = new IPAddress(v6).ToString();
                break;
            default:
                throw new InvalidDataException("unknown address type");
        }

        byte[] portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes, _stop.Token);
        return (host, (portBytes[0] << 8) | portBytes[1]);
    }

    private async Task<string> ReadUntilNulAsync(NetworkStream stream)
    {
        var text = new System.Text.StringBuilder();
        byte[] one = new byte[1];
        while (text.Length < 256)
        {
            await stream.ReadExactlyAsync(one, _stop.Token);
            if (one[0] == 0)
            {
                return text.ToString();
            }

            text.Append((char)one[0]);
        }

        throw new InvalidDataException("name too long");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try { await _accepting; } catch { /* stopped */ }
        _stop.Dispose();
    }
}
