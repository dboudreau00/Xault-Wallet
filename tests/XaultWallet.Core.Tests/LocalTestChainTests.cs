using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using XaultWallet.Core.Monero;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// monero-wallet-rpc's --allow-mismatched-daemon-version is passed ONLY for the user's own private
/// test chain: a node on this machine that itself says it is a regtest ("fakechain") chain.
/// </summary>
public class LocalTestChainTests
{
    [Theory]
    [InlineData("http://127.0.0.1:18081", true)]
    [InlineData("http://127.4.5.6:18081", true)]
    [InlineData("http://localhost:18081", true)]
    [InlineData("http://LOCALHOST:18081", true)]
    [InlineData("http://[::1]:18081", true)]
    [InlineData("https://127.0.0.1/monero", true)]
    [InlineData("http://192.168.1.10:18081", false)]
    [InlineData("http://10.0.0.2:18081", false)]
    [InlineData("http://0.0.0.0:18081", false)]
    [InlineData("http://node.example.com:18081", false)]
    [InlineData("http://localhost.example.com:18081", false)]
    [InlineData("http://127.0.0.1.nip.io:18081", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_Addresses_On_This_Machine_Are_Loopback(string? address, bool expected) =>
        Assert.Equal(expected, DaemonAddress.IsLoopback(address));

    [Theory]
    [InlineData("fakechain", true)]
    [InlineData("mainnet", false)]
    [InlineData("stagenet", false)]
    [InlineData("testnet", false)]
    [InlineData("FAKECHAIN", false)] // exact value only, as monerod writes it
    public async Task A_Local_Node_Is_A_Test_Chain_Only_When_It_Says_Fakechain(string nettype, bool expected)
    {
        using var node = new CannedHttpServer("{\"status\":\"OK\",\"height\":75,\"nettype\":\"" + nettype + "\"}");
        Assert.Equal(expected, await MoneroDiagnostics.IsLocalTestChainAsync(node.Url, proxyAddress: null));
        Assert.Contains("/get_info", node.RequestLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"status\":\"OK\",\"height\":75}")]           // no nettype at all (old or odd node)
    [InlineData("{\"nettype\":42}")]                           // wrong type
    [InlineData("[\"fakechain\"]")]                            // not an object
    [InlineData("<html>fakechain</html>")]                     // not JSON
    public async Task Anything_Unexpected_Reads_As_Not_A_Test_Chain(string body)
    {
        using var node = new CannedHttpServer(body);
        Assert.False(await MoneroDiagnostics.IsLocalTestChainAsync(node.Url, proxyAddress: null));
    }

    [Fact]
    public async Task An_Unreachable_Local_Node_Is_Not_A_Test_Chain()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens there now

        Assert.False(await MoneroDiagnostics.IsLocalTestChainAsync($"http://127.0.0.1:{port}", proxyAddress: null));
    }

    [Fact]
    public async Task A_Remote_Node_Is_Never_Even_Asked()
    {
        // 192.0.2.0/24 is TEST-NET-1 (RFC 5737): nothing answers there, so a probe would hang until
        // its timeout. Returning at once proves no request was attempted for a non-loopback node.
        var sw = Stopwatch.StartNew();
        Assert.False(await MoneroDiagnostics.IsLocalTestChainAsync("http://192.0.2.1:18081", proxyAddress: null));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
    }

    /// <summary>Answers one HTTP request with a fixed 200 JSON body and records the request line.</summary>
    private sealed class CannedHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serve;

        public CannedHttpServer(string body)
        {
            _listener.Start();
            _serve = Task.Run(async () =>
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                RequestLine = await reader.ReadLineAsync() ?? string.Empty;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                {
                    // skip the headers
                }

                byte[] content = Encoding.UTF8.GetBytes(body);
                byte[] head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(content);
            });
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public string RequestLine { get; private set; } = string.Empty;

        public void Dispose()
        {
            try { _serve.Wait(TimeSpan.FromSeconds(5)); } catch { /* the test already has its answer */ }
            _listener.Stop();
        }
    }
}
