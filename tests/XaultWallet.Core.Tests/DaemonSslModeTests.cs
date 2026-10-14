using XaultWallet.Core.Monero;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>An https:// node is held to TLS (no plain-HTTP fallback); every other address keeps wallet-rpc's default.</summary>
public class DaemonSslModeTests
{
    [Theory]
    [InlineData("https://node.example.org:18089")]
    [InlineData("  https://node.example.org  ")]
    [InlineData("HTTPS://Node.Example.org:443")]
    public void Https_Nodes_Require_Tls(string address)
    {
        Assert.Equal("enabled", MoneroProcessManager.DaemonSslMode(address));
        // monero-wallet-rpc refuses "enabled" with only system CAs: it needs this (or a pin) to open a wallet.
        Assert.True(MoneroProcessManager.DaemonSslAllowAnyCert(address));
    }

    [Theory]
    [InlineData("http://node.example.org:18081")]
    [InlineData("http://abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrstuv.onion:18081")]
    [InlineData("node.example.org:18081")]
    [InlineData("127.0.0.1:18081")]
    [InlineData("")]
    public void Other_Addresses_Keep_The_Default(string address)
    {
        Assert.Equal("autodetect", MoneroProcessManager.DaemonSslMode(address));
        Assert.False(MoneroProcessManager.DaemonSslAllowAnyCert(address));
    }
}

/// <summary>The wallet-rpc arguments that say how the node is reached.</summary>
public class DaemonConnectionArgumentsTests
{
    private static string Joined(string address, string? proxy) =>
        string.Join(' ', MoneroProcessManager.DaemonConnectionArguments(address, proxy));

    [Fact]
    public void A_Direct_Http_Node_Adds_Nothing_To_The_Address() =>
        Assert.Equal("--daemon-address http://node.example.org:18081", Joined("  http://node.example.org:18081 ", null));

    [Fact]
    public void An_Https_Node_Is_Held_To_Tls() =>
        Assert.Equal("--daemon-address https://node.example.org:18089 --daemon-ssl enabled --daemon-ssl-allow-any-cert",
            Joined("https://node.example.org:18089", null));

    [Theory]
    [InlineData("http://node.example.org:18081")]
    [InlineData("http://127.0.0.1:18081")]
    [InlineData("http://abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrstuvwx.onion:18089")]
    [InlineData("http://abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrstuvwx.onion")]
    public void Any_Node_Through_A_Proxy_Needs_The_Switch_Wallet_Rpc_Demands(string address) =>
        Assert.Equal($"--daemon-address {address} --daemon-ssl-allow-any-cert --proxy 127.0.0.1:9050", Joined(address, "127.0.0.1:9050"));

    [Theory]
    [InlineData(null)]
    [InlineData("127.0.0.1:9050")]
    public void The_Switch_Is_Never_Given_Twice(string? proxy)
    {
        // wallet-rpc refuses to start when the same switch is repeated.
        var arguments = MoneroProcessManager.DaemonConnectionArguments("https://node.example.org:18089", proxy);
        Assert.Equal(1, arguments.Count(a => a == "--daemon-ssl-allow-any-cert"));
        Assert.Equal(proxy is null ? 5 : 7, arguments.Count);
    }

    [Fact]
    public void The_Proxy_Comes_Last_And_Is_Not_Trimmed_Away() =>
        Assert.EndsWith("--proxy 127.0.0.1:9150", Joined("https://node.example.org", "127.0.0.1:9150"));
}
