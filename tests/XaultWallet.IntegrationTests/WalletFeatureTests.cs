using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;
using Xunit;
using Xunit.Abstractions;

namespace XaultWallet.IntegrationTests;

/// <summary>
/// 0.5's wallet features against the REAL monero-wallet-rpc: wallets restored from keys and
/// watch-only, subaddresses and accounts that survive a lock, one transaction paying several
/// recipients, message signatures and reserve proofs. Funds flows need a regtest node (mining).
/// </summary>
public sealed class WalletFeatureTests
{
    private readonly ITestOutputHelper _out;

    public WalletFeatureTests(ITestOutputHelper output) => _out = output;

    private bool Skip(bool needsMining = false)
    {
        if (IntegrationEnv.Configured && (!needsMining || IntegrationEnv.IsRegtest))
        {
            return false;
        }

        _out.WriteLine(needsMining ? "SKIPPED: needs XW_NETWORK=regtest (it mines blocks)." : IntegrationEnv.SkipReason);
        return true;
    }

    private static WalletSecrets Seed(string mnemonic, ulong height) => MoneroIntegrationTests.Secrets(mnemonic, height);

    private static async Task<WalletSecrets> NewSeedWalletAsync(MoneroWalletService svc)
    {
        (string mnemonic, ulong height) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        return Seed(mnemonic, height);
    }

    [Fact]
    public async Task Wallets_Restored_From_Keys_And_Watch_Only_Have_The_Seeds_Address()
    {
        if (Skip()) { return; }

        await using MoneroWalletService svc = IntegrationEnv.NewService();
        WalletSecrets seed = await NewSeedWalletAsync(svc);
        await svc.OpenAsync(seed);
        string address = await svc.GetPrimaryAddressAsync();
        (string mnemonic, string viewKey, string spendKey) = await svc.GetKeysAsync();
        await svc.CloseAsync();

        Assert.Equal(seed.Mnemonic, mnemonic.Trim());
        Assert.Matches("^[0-9a-f]{64}$", viewKey);
        Assert.Matches("^[0-9a-f]{64}$", spendKey);

        var keys = new WalletSecrets
        {
            Kind = WalletKind.Keys,
            Network = seed.Network,
            Address = address,
            ViewKey = viewKey,
            SpendKey = spendKey,
            RestoreHeight = seed.RestoreHeight,
            DaemonAddress = seed.DaemonAddress,
            EphemeralWalletPassword = seed.EphemeralWalletPassword,
        };
        Assert.Equal(address, await svc.ValidateWalletOpensAsync(keys));

        var watch = new WalletSecrets
        {
            Kind = WalletKind.ViewOnly,
            Network = seed.Network,
            Address = address,
            ViewKey = viewKey,
            RestoreHeight = seed.RestoreHeight,
            DaemonAddress = seed.DaemonAddress,
            EphemeralWalletPassword = seed.EphemeralWalletPassword,
        };
        Assert.Equal(address, await svc.ValidateWalletOpensAsync(watch));

        // A view-only wallet has no spend key to give out.
        await svc.OpenAsync(watch);
        (_, string watchView, string watchSpend) = await svc.GetKeysAsync();
        Assert.Equal(viewKey, watchView);
        Assert.True(string.IsNullOrEmpty(watchSpend.Trim('0')), "a watch-only wallet reported a spend key");
        await svc.CloseAsync();

        // Keys that don't belong to the address are refused. (monero-wallet-rpc itself accepts them:
        // generate_from_keys never checks, which is why the app proves each key.)
        WalletSecrets other = await NewSeedWalletAsync(svc);
        await svc.OpenAsync(other);
        (_, string otherView, string otherSpend) = await svc.GetKeysAsync();
        await svc.CloseAsync();

        watch.ViewKey = otherView;
        var wrongView = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ValidateWalletOpensAsync(watch));
        Assert.Contains("view key doesn't belong", wrongView.Message);

        keys.SpendKey = otherSpend;
        var wrongSpend = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ValidateWalletOpensAsync(keys));
        Assert.Contains("spend key doesn't belong", wrongSpend.Message);
    }

    [Fact]
    public async Task Subaddresses_And_Accounts_Handed_Out_Before_A_Lock_Come_Back()
    {
        if (Skip()) { return; }

        await using MoneroWalletService svc = IntegrationEnv.NewService();
        WalletSecrets wallet = await NewSeedWalletAsync(svc);
        wallet.SubaddressCounts[0] = 5;
        wallet.SubaddressCounts[2] = 3;

        await svc.OpenAsync(wallet);
        Assert.Equal(3, (await svc.GetAccountsAsync()).Accounts.Count);
        IReadOnlyList<AddressInfo> main = await svc.GetAddressesAsync(0);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, main.Select(a => a.AddressIndex));
        Assert.Equal(3, (await svc.GetAddressesAsync(2)).Count);

        // The next subaddress continues the numbering: never one already given to someone.
        (uint index, string address) = await svc.NewSubaddressAsync(0, string.Empty);
        Assert.Equal(5u, index);
        Assert.DoesNotContain(address, main.Select(a => a.Address));

        (uint account, _) = await svc.NewAccountAsync();
        Assert.Equal(3u, account);
    }

    [Fact]
    public async Task A_Wallet_That_Handed_Out_Many_Subaddresses_Still_Opens()
    {
        if (Skip()) { return; }

        // monero-wallet-rpc up to v0.18.4.2 creates at most 64 per call; 150 takes three.
        await using MoneroWalletService svc = IntegrationEnv.NewService();
        WalletSecrets wallet = await NewSeedWalletAsync(svc);
        wallet.SubaddressCounts[1] = 150;

        await svc.OpenAsync(wallet);
        IReadOnlyList<AddressInfo> addresses = await svc.GetAddressesAsync(1);
        Assert.Equal(150, addresses.Count);
        Assert.Equal(150, addresses.Select(a => a.Address).Distinct().Count());
        (uint index, _) = await svc.NewSubaddressAsync(1, string.Empty);
        Assert.Equal(150u, index);
    }

    [Fact]
    public async Task One_Transaction_Pays_Two_Subaddresses_And_Proofs_Verify()
    {
        if (Skip(needsMining: true)) { return; }

        await using MoneroWalletService payer = IntegrationEnv.NewService();
        await using MoneroWalletService payee = IntegrationEnv.NewService();
        WalletSecrets a = await NewSeedWalletAsync(payer);
        WalletSecrets b = await NewSeedWalletAsync(payee);
        b.SubaddressCounts[0] = 3;

        await payer.OpenAsync(a);
        string addressA = await payer.GetPrimaryAddressAsync();
        await IntegrationEnv.MineAsync(addressA, 75); // coinbase unlocks after 60 blocks
        await EventuallyAsync(async () =>
        {
            await payer.RefreshAsync();
            return (await payer.GetBalanceAsync()).unlocked > 10m;
        }, "the payer's mined coins to unlock");

        await payee.OpenAsync(b);
        IReadOnlyList<AddressInfo> subs = await payee.GetAddressesAsync(0);
        Assert.Equal(3, subs.Count);
        Assert.All(subs, s => Assert.False(s.Used));

        TransferResult tx = await payer.PrepareSendAsync([(subs[1].Address, 1.5m), (subs[2].Address, 2.25m)], 0, 1);
        Assert.Equal(3.75m, MoneroRpcClient.AtomicToXmr(tx.Amount));
        Assert.True(tx.Fee > 0);
        string hash = await payer.RelaySendAsync(tx.TxMetadata);
        _out.WriteLine($"sent {hash}, fee {MoneroRpcClient.AtomicToXmr(tx.Fee)}");

        await IntegrationEnv.MineAsync(addressA, 12);
        await EventuallyAsync(async () =>
        {
            await payee.RefreshAsync();
            return (await payee.GetBalanceAsync()).balance == 3.75m;
        }, "both payments to arrive");

        IReadOnlyList<AddressInfo> after = await payee.GetAddressesAsync(0);
        Assert.False(after[0].Used);
        Assert.True(after[1].Used);
        Assert.True(after[2].Used);
        IReadOnlyList<TransferEntry> history = await payee.GetHistoryAsync(0);
        Assert.Equal(new uint[] { 1, 2 }, history.Where(t => t.TxId == hash).Select(t => t.SubaddrIndex.Minor).OrderBy(i => i));

        // Message signature: proves control of the address, and nothing else verifies.
        string signature = await payer.SignMessageAsync("I own this address");
        Assert.True(await payee.VerifyMessageAsync("I own this address", addressA, signature));
        Assert.False(await payee.VerifyMessageAsync("I own this addresS", addressA, signature));
        Assert.False(await payee.VerifyMessageAsync("I own this address", subs[0].Address, signature));

        // Reserve proof: the payee proves it holds what it received.
        await IntegrationEnv.MineAsync(addressA, 10);
        await payee.RefreshAsync();
        string proof = await payee.GetReserveProofAsync(null, 0, "audit 2026");
        (bool good, decimal total, decimal spent) = await payer.CheckReserveProofAsync(subs[0].Address, "audit 2026", proof);
        Assert.True(good);
        Assert.Equal(3.75m, total);
        Assert.Equal(0m, spent);
        (bool forged, _, _) = await payer.CheckReserveProofAsync(subs[0].Address, "audit 2027", proof);
        Assert.False(forged);
    }

    [Fact]
    public async Task Node_Can_Be_Changed_While_The_Wallet_Is_Open()
    {
        if (Skip()) { return; }

        await using MoneroWalletService svc = IntegrationEnv.NewService();
        WalletSecrets wallet = await NewSeedWalletAsync(svc);
        await svc.OpenAsync(wallet);
        await svc.SetDaemonAsync(IntegrationEnv.Daemon!);
        await svc.RefreshAsync();
        Assert.True(await svc.GetHeightAsync() > 0);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetDaemonAsync("not a url"));
    }

    private async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException("Timed out waiting for " + what);
    }
}
