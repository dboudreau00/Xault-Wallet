using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using Xunit;
using Xunit.Abstractions;

namespace XaultWallet.IntegrationTests;

/// <summary>
/// Coin control against the REAL monero-wallet-rpc on a private regtest chain: the coins a wallet
/// holds, freezing one so no transaction spends it (Send max leaves it out too), and freezing it
/// again after a lock, when the backend restored from the seed knows nothing of the freeze.
/// </summary>
public sealed class CoinControlTests
{
    private readonly ITestOutputHelper _out;

    public CoinControlTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task A_Frozen_Coin_Is_Never_Spent_And_Is_Frozen_Again_After_A_Lock()
    {
        if (!IntegrationEnv.Configured || !IntegrationEnv.IsRegtest)
        {
            _out.WriteLine("SKIPPED: needs XW_NETWORK=regtest (it mines blocks).");
            return;
        }

        await using MoneroWalletService payer = IntegrationEnv.NewService();
        await using MoneroWalletService holder = IntegrationEnv.NewService();
        (string m1, ulong h1) = await payer.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        (string m2, ulong h2) = await holder.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        WalletSecrets a = MoneroIntegrationTests.Secrets(m1, h1);
        WalletSecrets b = MoneroIntegrationTests.Secrets(m2, h2);

        await payer.OpenAsync(a);
        string payerAddress = await payer.GetPrimaryAddressAsync();
        await IntegrationEnv.MineAsync(payerAddress, 75); // coinbase unlocks after 60 blocks
        await EventuallyAsync(async () =>
        {
            await payer.RefreshAsync();
            return (await payer.GetBalanceAsync()).unlocked > 10m;
        }, "the payer's mined coins to unlock");

        // Two payments, two transactions: two coins of 1 and 2 XMR.
        await holder.OpenAsync(b);
        string holderAddress = await holder.GetPrimaryAddressAsync();
        foreach (decimal amount in new[] { 1m, 2m })
        {
            TransferResult tx = await payer.PrepareSendAsync([(holderAddress, amount)], 0, 1);
            await payer.RelaySendAsync(tx.TxMetadata);
            await IntegrationEnv.MineAsync(payerAddress, 1);
            await payer.RefreshAsync();
        }

        await IntegrationEnv.MineAsync(payerAddress, 12); // received outputs unlock after 10 blocks
        await EventuallyAsync(async () =>
        {
            await holder.RefreshAsync();
            return (await holder.GetBalanceAsync()).unlocked == 3m;
        }, "both coins to arrive and unlock");

        IReadOnlyList<OwnedOutput> coins = await holder.GetCoinsAsync(0);
        Assert.Equal(new ulong[] { 2_000_000_000_000, 1_000_000_000_000 }, coins.Select(c => c.Amount));
        Assert.All(coins, c => Assert.Matches("^[0-9a-f]{64}$", c.KeyImage));
        Assert.All(coins, c => Assert.True(c.Unlocked && !c.Frozen && c.BlockHeight > 0));
        string big = coins[0].KeyImage;

        await holder.SetFrozenAsync(big, frozen: true);
        Assert.True((await holder.GetCoinsAsync(0)).Single(c => c.KeyImage == big).Frozen);
        (decimal balance, decimal unlocked) = await holder.GetBalanceAsync();
        _out.WriteLine($"with 2 XMR frozen: balance {balance}, unlocked {unlocked}");

        // More than the unfrozen coin: wallet-rpc refuses (it may not touch the frozen one).
        await Assert.ThrowsAsync<MoneroRpcClient.MoneroRpcException>(() => holder.PrepareSendAsync([(payerAddress, 1.5m)], 0, 1));

        // Send max sweeps the 1 XMR coin only.
        SweepAllResult sweep = await holder.PrepareSweepAllAsync(payerAddress, 0, 1);
        decimal swept = MoneroRpcClient.AtomicToXmr((ulong)sweep.AmountList.Sum(x => (decimal)x));
        _out.WriteLine($"sweep with the 2 XMR coin frozen: {swept} XMR");
        Assert.InRange(swept, 0.9m, 1m);

        // A lock: the backend restored from the seed has no freeze. Re-applying the vault's list does.
        await holder.CloseAsync();
        await holder.OpenAsync(b);
        await EventuallyAsync(async () =>
        {
            await holder.RefreshAsync();
            return (await holder.GetCoinsAsync(0)).Count == 2;
        }, "the restored wallet to find its coins again");
        Assert.False((await holder.GetCoinsAsync(0)).Single(c => c.KeyImage == big).Frozen);
        Assert.Equal(1, await holder.ApplyFrozenAsync([big, new string('e', 64)])); // an unknown coin is skipped, not an error
        Assert.True((await holder.GetCoinsAsync(0)).Single(c => c.KeyImage == big).Frozen);
        Assert.InRange(MoneroRpcClient.AtomicToXmr((ulong)(await holder.PrepareSweepAllAsync(payerAddress, 0, 1)).AmountList.Sum(x => (decimal)x)), 0.9m, 1m);

        // Unfrozen, it can be spent again.
        await holder.SetFrozenAsync(big, frozen: false);
        TransferResult spend = await holder.PrepareSendAsync([(payerAddress, 1.5m)], 0, 1);
        Assert.True(spend.Fee > 0);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
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
