using XaultWallet.Core.Models;

namespace XaultWallet.IntegrationTests;

/// <summary>
/// Reads integration configuration from environment variables. When the binary/daemon
/// aren't configured, <see cref="Configured"/> is false and the tests short-circuit.
///
///   XW_WALLET_RPC  absolute path to monero-wallet-rpc
///   XW_DAEMON      daemon address, e.g. http://127.0.0.1:38081  (stagenet)
///   XW_NETWORK     mainnet | stagenet | testnet   (default: stagenet)
/// </summary>
internal static class IntegrationEnv
{
    public static string? WalletRpc => Get("XW_WALLET_RPC");
    public static string? Daemon => Get("XW_DAEMON");

    public static MoneroNetwork Network => (Get("XW_NETWORK")?.ToLowerInvariant()) switch
    {
        "mainnet" => MoneroNetwork.Mainnet,
        "testnet" => MoneroNetwork.Testnet,
        _ => MoneroNetwork.Stagenet,
    };

    public static bool Configured =>
        !string.IsNullOrWhiteSpace(WalletRpc) && !string.IsNullOrWhiteSpace(Daemon);

    public const string SkipReason =
        "SKIPPED: integration test not configured. Set XW_WALLET_RPC and XW_DAEMON " +
        "(and optionally XW_NETWORK) to run this against a real node.";

    private static string? Get(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}
