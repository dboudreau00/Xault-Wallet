using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace XaultWallet.E2E;

/// <summary>
/// The private regtest chain the scenario runs on (<c>monerod --regtest --offline</c>). Mining is the
/// only thing done outside the UI: it stands in for "someone pays this wallet".
/// </summary>
public sealed class TestChain : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };
    private readonly Uri _jsonRpc;

    public TestChain(string daemonUrl)
    {
        DaemonUrl = daemonUrl.TrimEnd('/');
        _jsonRpc = new Uri(DaemonUrl + "/json_rpc");
    }

    public string DaemonUrl { get; }

    /// <summary>Mine <paramref name="blocks"/> blocks paying their rewards to <paramref name="address"/>.</summary>
    public async Task MineAsync(string address, int blocks)
    {
        // StringContent, not PostAsJsonAsync: a chunked body reads as empty to monerod's HTTP server.
        string body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "0",
            method = "generateblocks",
            @params = new { amount_of_blocks = blocks, wallet_address = address },
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await _http.PostAsync(_jsonRpc, content);
        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("error", out JsonElement err))
        {
            throw new InvalidOperationException("generateblocks failed: " + err);
        }
    }

    public async Task<ulong> HeightAsync()
    {
        using JsonDocument doc = JsonDocument.Parse(await _http.GetStringAsync(DaemonUrl + "/get_height"));
        return doc.RootElement.GetProperty("height").GetUInt64();
    }

    public void Dispose() => _http.Dispose();
}
