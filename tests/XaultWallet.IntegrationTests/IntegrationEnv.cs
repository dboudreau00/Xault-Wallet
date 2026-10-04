using System.Text.Json;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;

namespace XaultWallet.IntegrationTests;

/// <summary>
/// Reads integration configuration from environment variables. When the binary/daemon
/// aren't configured, <see cref="Configured"/> is false and the tests short-circuit.
///
///   XW_WALLET_RPC  absolute path to monero-wallet-rpc
///   XW_DAEMON      daemon address, e.g. http://127.0.0.1:38081  (stagenet)
///   XW_NETWORK     mainnet | stagenet | testnet | regtest   (default: stagenet)
///
/// regtest = a private chain from <c>monerod --regtest --offline --fixed-difficulty 1</c>. It uses
/// mainnet address formats, needs --allow-mismatched-daemon-version on the wallet side, and lets the
/// funds-flow tests mine blocks on demand (see <see cref="MineAsync"/>). CI runs exactly that.
/// </summary>
internal static class IntegrationEnv
{
    public static string? WalletRpc => Get("XW_WALLET_RPC");
    public static string? Daemon => Get("XW_DAEMON");

    private static string NetworkName => Get("XW_NETWORK")?.ToLowerInvariant() ?? "stagenet";

    public static bool IsRegtest => NetworkName == "regtest";

    public static MoneroNetwork Network => NetworkName switch
    {
        "mainnet" or "regtest" => MoneroNetwork.Mainnet,
        "testnet" => MoneroNetwork.Testnet,
        _ => MoneroNetwork.Stagenet,
    };

    // A regtest node on this machine is detected by the app itself (it reports "fakechain"), which is
    // exactly the path these tests should exercise; only a REMOTE regtest node needs the explicit flag.
    public static WalletRpcOptions Options => new() { AllowMismatchedDaemonVersion = IsRegtest && !DaemonAddress.IsLoopback(Daemon) };

    public static bool Configured =>
        !string.IsNullOrWhiteSpace(WalletRpc) && !string.IsNullOrWhiteSpace(Daemon);

    public const string SkipReason =
        "SKIPPED: integration test not configured. Set XW_WALLET_RPC and XW_DAEMON " +
        "(and optionally XW_NETWORK) to run this against a real node.";

    public static MoneroWalletService NewService() => new(WalletRpc!, Options);

    /// <summary>regtest only: mine <paramref name="blocks"/> blocks paying their coinbase to <paramref name="address"/>.</summary>
    public static async Task MineAsync(string address, int blocks)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };
        var body = new
        {
            jsonrpc = "2.0",
            id = "0",
            method = "generateblocks",
            @params = new { amount_of_blocks = blocks, wallet_address = address },
        };
        // StringContent, not PostAsJsonAsync: JsonContent streams with Transfer-Encoding: chunked,
        // which monerod's epee HTTP server reads as an empty body (-32600 Invalid Request).
        using var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await http.PostAsync(new Uri(new Uri(Daemon!), "json_rpc"), content);
        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("error", out JsonElement err))
        {
            throw new InvalidOperationException("generateblocks failed: " + err);
        }
    }

    private static string? Get(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}
