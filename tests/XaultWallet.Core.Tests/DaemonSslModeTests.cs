using XaultWallet.Core.Monero;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>An https:// node is held to verified TLS; every other address keeps wallet-rpc's default.</summary>
public class DaemonSslModeTests
{
    [Theory]
    [InlineData("https://node.example.org:18089")]
    [InlineData("  https://node.example.org  ")]
    [InlineData("HTTPS://Node.Example.org:443")]
    public void Https_Nodes_Require_Tls(string address) =>
        Assert.Equal("enabled", MoneroProcessManager.DaemonSslMode(address));

    [Theory]
    [InlineData("http://node.example.org:18081")]
    [InlineData("http://abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrstuv.onion:18081")]
    [InlineData("node.example.org:18081")]
    [InlineData("127.0.0.1:18081")]
    [InlineData("")]
    public void Other_Addresses_Keep_The_Default(string address) =>
        Assert.Equal("autodetect", MoneroProcessManager.DaemonSslMode(address));
}
