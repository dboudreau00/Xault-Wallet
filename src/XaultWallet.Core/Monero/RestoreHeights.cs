using XaultWallet.Core.Models;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Where a brand-new seed may start scanning. A restore height ABOVE the block holding a payment
/// hides that payment until the wallet is restored again with an earlier height — so a height taken
/// from a node is never trusted beyond what the clock says the chain can be.
/// </summary>
public static class RestoreHeights
{
    /// <summary>
    /// Blocks a new seed's restore height sits below the tip it is derived from (~1 day at 2-minute
    /// blocks). Absorbs a reorg or a node a little ahead of its peers; costs seconds of scanning.
    /// </summary>
    public const ulong SafetyMargin = 720;

    private const long SecondsPerBlock = 120; // DIFFICULTY_TARGET_V2

    /// <summary>
    /// The clock-based chain-height estimate wallet2 uses to cap a daemon's claimed target height
    /// (wallet2::get_approximate_blockchain_height, monero master as of 2026-10): the network's latest
    /// hard-fork block, plus one block per 120 s since that fork's timestamp, minus a fixed per-network
    /// correction. Constants copied from src/hardforks/hardforks.cpp and src/wallet/wallet2.cpp.
    /// </summary>
    public static ulong ApproximateTip(MoneroNetwork network, DateTimeOffset now)
    {
        (ulong forkBlock, long forkTime, ulong correction) = network switch
        {
            MoneroNetwork.Testnet => (1_983_520UL, 1652813400L, 26_600UL),
            MoneroNetwork.Stagenet => (1_151_720UL, 1656629118L, 48_600UL),
            _ => (2_689_608UL, 1656629118L, 33_600UL),
        };

        long t = now.ToUnixTimeSeconds();
        ulong estimate;
        if (t > forkTime)
        {
            estimate = forkBlock + (ulong)((t - forkTime) / SecondsPerBlock);
        }
        else
        {
            // A clock set before the latest fork: wallet2 counts back, and gives up (0) past genesis.
            ulong blocksUntilFork = (ulong)((forkTime - t) / SecondsPerBlock);
            if (forkBlock <= blocksUntilFork)
            {
                return 0;
            }

            estimate = forkBlock - blocksUntilFork;
        }

        return estimate > correction ? estimate - correction : estimate;
    }

    /// <summary>
    /// The height a seed created NOW may scan from, given the tip a node reports: the lower of that
    /// tip and <see cref="ApproximateTip"/>, minus <see cref="SafetyMargin"/>. A lying or broken node
    /// claiming a far-future height therefore cannot push the start past the clock's estimate. Taking
    /// the lower value only ever costs extra scanning, never a missed payment. A private regtest chain
    /// sits far below the estimate, so its real tip is used.
    /// </summary>
    public static ulong ForNewSeed(ulong reportedTip, MoneroNetwork network, DateTimeOffset now)
    {
        ulong trusted = Math.Min(reportedTip, ApproximateTip(network, now));
        return trusted > SafetyMargin ? trusted - SafetyMargin : 0;
    }
}
