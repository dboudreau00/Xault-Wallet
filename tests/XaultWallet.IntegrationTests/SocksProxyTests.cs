using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using Xunit;
using Xunit.Abstractions;

namespace XaultWallet.IntegrationTests;

/// <summary>
/// The Tor and SOCKS-proxy modes against the REAL monero-wallet-rpc: a stand-in proxy takes the place of Tor.
/// Through <c>--proxy</c> wallet-rpc refuses to open or create a wallet for any node it can't verify unless it
/// is told <c>--daemon-ssl-allow-any-cert</c>, so before the app passed it no clearnet node worked in these modes.
/// </summary>
public sealed class SocksProxyTests
{
    private readonly ITestOutputHelper _out;

    public SocksProxyTests(ITestOutputHelper output) => _out = output;

    private bool Skip()
    {
        if (IntegrationEnv.Configured)
        {
            return false;
        }

        _out.WriteLine(IntegrationEnv.SkipReason);
        return true;
    }

    [Fact]
    public async Task A_Wallet_Opens_And_Reaches_Its_Node_Through_A_Socks_Proxy()
    {
        if (Skip()) { return; }

        await using var proxy = new TestSocksProxy();
        WalletRpcOptions options = IntegrationEnv.Options with { ProxyAddress = proxy.Address };
        await using var svc = new MoneroWalletService(IntegrationEnv.WalletRpc!, options);

        (string mnemonic, ulong height) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        WalletSecrets wallet = MoneroIntegrationTests.Secrets(mnemonic, height);
        await svc.OpenAsync(wallet);
        await svc.RefreshAsync();
        Assert.True(await svc.GetHeightAsync() > 0);

        _out.WriteLine($"proxy saw {proxy.Socks4Connections} SOCKS4 and {proxy.Socks5Connections} SOCKS5 connections");
        Assert.True(proxy.Socks4Connections > 0, "monero-wallet-rpc reached its node through the proxy");
    }
}
