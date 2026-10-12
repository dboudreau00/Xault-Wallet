// The estimate in ApproximateTip (its constants and its method) is adapted from Monero:
// wallet2::get_approximate_blockchain_height in src/wallet/wallet2.cpp, with the hard-fork constants of
// src/hardforks/hardforks.cpp. Monero's license applies to that part:
//
// Copyright (c) 2014-2024, The Monero Project
//
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//
// 1. Redistributions of source code must retain the above copyright notice, this
// list of conditions and the following disclaimer.
//
// 2. Redistributions in binary form must reproduce the above copyright notice,
// this list of conditions and the following disclaimer in the documentation
// and/or other materials provided with the distribution.
//
// 3. Neither the name of the copyright holder nor the names of its contributors
// may be used to endorse or promote products derived from this software without
// specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//
// The rest of this file is under XaultWallet's own license (see LICENSE).

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

    /// <summary>
    /// How far above <see cref="ApproximateTip"/> a typed restore height may sit before it is refused
    /// while no node can be asked for the real tip (~139 days of blocks). The estimate runs
    /// deliberately low, so this only catches heights no chain could have reached (typos, a height
    /// from the wrong network), never a merely recent one.
    /// </summary>
    public const ulong UnverifiedTipSlack = 100_000;

    /// <summary>
    /// True when <paramref name="height"/> is beyond anything the clock says the chain can have
    /// reached (<see cref="ApproximateTip"/> + <see cref="UnverifiedTipSlack"/> + <see cref="SafetyMargin"/>).
    /// Used when the node's own tip is unknown: such a height would scan nothing and look like an
    /// empty wallet.
    /// </summary>
    public static bool IsBeyondPlausibleTip(ulong height, MoneroNetwork network, DateTimeOffset now)
    {
        ulong estimate = ApproximateTip(network, now);
        return estimate > 0 && height > estimate + UnverifiedTipSlack + SafetyMargin;
    }
}
