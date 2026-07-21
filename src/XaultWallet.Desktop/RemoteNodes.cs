namespace XaultWallet.Desktop;

/// <summary>A selectable public remote node preset.</summary>
public sealed record RemoteNode(string Name, string Url, int NetworkIndex)
{
    // Shown in the dropdown, e.g. "MoneroDevs — http://node.monerodevs.org:28089"
    public string Display => $"{Name} — {Url}";
}

/// <summary>
/// A small, curated set of reputable community/public Monero nodes, grouped by network.
///
/// These are THIRD-PARTY nodes: convenient, but the operator can see your IP and the
/// transactions you broadcast. They can also go offline or change. The Settings screen's
/// "Test node" button is the source of truth — always confirm a node responds before relying
/// on it. For mainnet privacy, run your own node instead.
///
/// Ports follow each operator's published convention (Rino uses the default 18081/38081;
/// MoneroDevs and Cake expose public RPC on the 18089/28089/38089 range).
/// </summary>
public static class RemoteNodes
{
    public static IReadOnlyList<RemoteNode> All { get; } = new[]
    {
        // ---- Mainnet (real funds) ----
        new RemoteNode("Cake Wallet (mainnet)",   "http://xmr-node.cakewallet.com:18081",  0),
        new RemoteNode("Rino (mainnet)",          "http://node.community.rino.io:18081",   0),
        new RemoteNode("MoneroDevs (mainnet)",    "http://node.monerodevs.org:18089",      0),
        new RemoteNode("SethForPrivacy (mainnet)","http://node.sethforprivacy.com:18089",  0),
        new RemoteNode("HashVault (mainnet)",     "http://nodes.hashvault.pro:18081",      0),

        // ---- Stagenet (mainnet-like, no value) ----
        new RemoteNode("Rino (stagenet)",         "http://stagenet.community.rino.io:38081", 1),
        new RemoteNode("MoneroDevs (stagenet)",   "http://node.monerodevs.org:38089",        1),

        // ---- Testnet (developer network, no value) ----
        new RemoteNode("MoneroDevs (testnet)",    "http://node.monerodevs.org:28089",     2),
    };
}
