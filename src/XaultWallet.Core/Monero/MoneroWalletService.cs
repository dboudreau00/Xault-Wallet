using XaultWallet.Core.Models;

namespace XaultWallet.Core.Monero;

/// <summary>
/// The application-facing wallet API. Owns the lifecycle: bring a wallet online
/// from its secrets, expose balance / address / send / history, and tear
/// everything down (shredding temp files) on lock.
/// </summary>
public sealed class MoneroWalletService : IAsyncDisposable
{
    private readonly string _walletRpcBinary;
    private readonly WalletRpcOptions _options;
    private MoneroProcessManager? _proc;
    private MoneroRpcClient? _rpc;
    private string _daemon = string.Empty; // the open wallet's node

    public bool IsOpen => _rpc is not null;

    /// <summary>True when the backend wallet-rpc process died underneath an open wallet
    /// (crash, external kill). Distinguishes "backend is gone" from "node is slow" so the
    /// UI can offer a restart instead of surfacing repeated connection errors.</summary>
    public bool BackendExited => _proc?.HasProcessExited == true;

    /// <summary>The open wallet syncs from the user's own private test chain (a local regtest node).</summary>
    public bool IsLocalTestChain => _proc?.IsLocalTestChain == true;

    /// <param name="walletRpcBinary">Path to monero-wallet-rpc.</param>
    /// <param name="proxyAddress">Optional SOCKS proxy ("host:port") for the backend's daemon
    /// traffic; null/empty = direct connection.</param>
    public MoneroWalletService(string walletRpcBinary, string? proxyAddress = null)
        : this(walletRpcBinary, new WalletRpcOptions { ProxyAddress = proxyAddress })
    {
    }

    public MoneroWalletService(string walletRpcBinary, WalletRpcOptions options)
    {
        _walletRpcBinary = walletRpcBinary;
        ArgumentNullException.ThrowIfNull(options);
        _options = options with
        {
            ProxyAddress = string.IsNullOrWhiteSpace(options.ProxyAddress) ? null : options.ProxyAddress.Trim(),
        };
    }

    /// <summary>Blocks a new seed's restore height sits below the tip (see <see cref="RestoreHeights"/>).</summary>
    public const ulong GeneratedSeedRestoreMargin = RestoreHeights.SafetyMargin;

    /// <summary>
    /// Generate a brand-new Monero wallet and return its 25-word mnemonic plus a restore height.
    /// Runs a throwaway monero-wallet-rpc instance in its own session dir, creates a deterministic
    /// wallet, reads back the seed, then shreds everything. Nothing touches persistent storage —
    /// the caller decides whether to seal the seed into the vault.
    ///
    /// The restore height comes from the DAEMON's tip, read BEFORE the seed exists (so no payment
    /// to it can sit below that height), capped by the clock-based chain estimate and lowered by
    /// <see cref="GeneratedSeedRestoreMargin"/> (<see cref="RestoreHeights.ForNewSeed"/>). It is 0
    /// (= full scan, always safe) when the daemon is unreachable. Note: wallet-rpc's own get_height
    /// right after create_wallet reports its local chain (1 on a fresh wallet), not the tip —
    /// sealing that made every unlock of a "new" wallet rescan the whole chain.
    /// </summary>
    public async Task<(string mnemonic, ulong restoreHeight)> GenerateNewSeedAsync(
        MoneroNetwork network, string daemonAddress, CancellationToken ct = default)
    {
        ulong tip = 0;
        try
        {
            tip = await MoneroDiagnostics.ProbeDaemonAsync(daemonAddress, _options.ProxyAddress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Unreachable/odd daemon — one that doesn't answer in time included (HttpClient reports its
            // timeout as a cancellation): fall back to 0. A full scan later is slow but never loses funds.
        }

        await using var proc = new MoneroProcessManager(_walletRpcBinary, _options);
        using MoneroRpcClient rpc = await proc.StartServerAsync(network, daemonAddress, ct).ConfigureAwait(false);

        // The seed comes back in a response: an impostor on the port could hand us a seed it knows.
        proc.EnsureBackendIsOurs();
        string ephemeralPw = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        await rpc.CreateWalletAsync("gen", ephemeralPw, "English", ct).ConfigureAwait(false);

        string mnemonic = (await rpc.QueryKeyAsync("mnemonic", ct).ConfigureAwait(false)).Key;

        try { await rpc.CloseWalletAsync(ct).ConfigureAwait(false); } catch { /* closing is best-effort */ }

        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            throw new InvalidOperationException("monero-wallet-rpc returned an empty mnemonic.");
        }

        return (mnemonic.Trim(), tip == 0 ? 0 : RestoreHeights.ForNewSeed(tip, network, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Confirm a seed actually opens into a valid wallet BEFORE it is sealed into the vault.
    /// Prevents the "imported a typo'd seed, now the wallet won't unlock" failure class.
    /// Fast: opening loads the keys; getting the address does not require chain sync.
    /// Returns the primary address on success; throws on an invalid seed.
    /// </summary>
    public Task<string> ValidateWalletOpensAsync(WalletSecrets secrets, CancellationToken ct = default) =>
        ValidateSeedOpensAsync(secrets, ct);

    /// <summary>Like <see cref="ValidateWalletOpensAsync"/> (the name predates key-restored wallets:
    /// it validates those too, and checks their keys against the address — see VerifyKeysAsync).</summary>
    public async Task<string> ValidateSeedOpensAsync(WalletSecrets secrets, CancellationToken ct = default)
    {
        await using var proc = new MoneroProcessManager(_walletRpcBinary, _options);
        using MoneroRpcClient rpc = await proc.StartFromSeedAsync(secrets, ct).ConfigureAwait(false);
        if (secrets.Kind != WalletKind.Seed)
        {
            await VerifyKeysAsync(rpc, secrets, ct).ConfigureAwait(false);
        }

        GetAddressResult addr = await rpc.GetAddressAsync(0, ct).ConfigureAwait(false);
        try { await rpc.CloseWalletAsync(ct).ConfigureAwait(false); } catch { }
        return addr.Address;
    }

    /// <summary>
    /// generate_from_keys only parses the keys it is given; it never checks that they belong to the
    /// address (monero-wallet-cli does, the RPC doesn't). A view key that doesn't would make a wallet
    /// that silently never sees a single payment. Loading a wallet file DOES check (wallet2::load_keys
    /// compares each secret key with the address's public key), so close the freshly generated wallet
    /// and open it again: Monero's own code then accepts or refuses the keys.
    /// </summary>
    private async Task VerifyKeysAsync(MoneroRpcClient rpc, WalletSecrets secrets, CancellationToken ct)
    {
        if (await ReopensAsync(rpc, secrets, ct).ConfigureAwait(false))
        {
            return;
        }

        // Say which key is wrong: a watch-only copy checks the view key alone.
        bool viewKeyFits = secrets.Kind == WalletKind.Keys
            && await ViewKeyFitsAsync(secrets, ct).ConfigureAwait(false);
        throw new InvalidOperationException(viewKeyFits
            ? "That private spend key doesn't belong to this address."
            : "That private view key doesn't belong to this address.");
    }

    private static async Task<bool> ReopensAsync(MoneroRpcClient rpc, WalletSecrets secrets, CancellationToken ct)
    {
        await rpc.CloseWalletAsync(ct).ConfigureAwait(false);
        try
        {
            await rpc.OpenWalletAsync(MoneroProcessManager.WalletFileName, secrets.EphemeralWalletPassword, ct).ConfigureAwait(false);
            return true;
        }
        catch (MoneroRpcClient.MoneroRpcException ex) when (ex.Code != 401)
        {
            // wallet-rpc reports the key check as a generic "Failed to open wallet" (its
            // open_wallet replaces wallet2's "does not correspond" message). The file was written a
            // moment ago, in our own session folder, with this very password: the keys are what
            // failed.
            return false;
        }
    }

    private async Task<bool> ViewKeyFitsAsync(WalletSecrets secrets, CancellationToken ct)
    {
        var watch = new WalletSecrets
        {
            Kind = WalletKind.ViewOnly,
            Network = secrets.Network,
            Address = secrets.Address,
            ViewKey = secrets.ViewKey,
            RestoreHeight = secrets.RestoreHeight,
            DaemonAddress = secrets.DaemonAddress,
            EphemeralWalletPassword = secrets.EphemeralWalletPassword,
        };
        await using var proc = new MoneroProcessManager(_walletRpcBinary, _options);
        using MoneroRpcClient rpc = await proc.StartFromSeedAsync(watch, ct).ConfigureAwait(false);
        return await ReopensAsync(rpc, watch, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bring a wallet online: restore it (from seed or keys) in its own backend, then recreate the
    /// accounts and subaddresses it had handed out before it was last locked
    /// (<see cref="WalletSecrets.SubaddressCounts"/>), so the next new subaddress continues where
    /// the last session stopped instead of handing an already-given address to someone else.
    /// </summary>
    public async Task OpenAsync(WalletSecrets secrets, CancellationToken ct = default)
    {
        await CloseAsync().ConfigureAwait(false);
        var proc = new MoneroProcessManager(_walletRpcBinary, _options);
        MoneroRpcClient? rpc = null;
        try
        {
            rpc = await proc.StartFromSeedAsync(secrets, ct).ConfigureAwait(false);
            await RestoreAddressesAsync(rpc, secrets.SubaddressCounts, ct).ConfigureAwait(false);
            _daemon = secrets.DaemonAddress;
            _rpc = rpc;
            _proc = proc; // only assign once fully started, so a failed open leaves us cleanly closed
        }
        catch
        {
            rpc?.Dispose();
            await proc.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Create accounts and subaddresses until each account has at least the recorded count.
    /// Creating them only extends the list the wallet shows: scanning finds payments to any
    /// subaddress within its lookahead regardless.</summary>
    private static async Task RestoreAddressesAsync(MoneroRpcClient rpc, IReadOnlyDictionary<uint, uint> counts, CancellationToken ct)
    {
        if (counts.Count == 0)
        {
            return;
        }

        const uint MaxAccounts = 1000;
        const uint MaxPerAccount = 10_000;
        uint accountsNeeded = Math.Min(counts.Keys.Max() + 1, MaxAccounts);
        uint accounts = (uint)(await rpc.GetAccountsAsync(ct).ConfigureAwait(false)).Accounts.Count;
        while (accounts < accountsNeeded)
        {
            await rpc.CreateAccountAsync(string.Empty, ct).ConfigureAwait(false);
            accounts++;
        }

        foreach ((uint account, uint wanted) in counts.OrderBy(kv => kv.Key))
        {
            if (account >= accountsNeeded)
            {
                continue;
            }

            uint have = (uint)(await rpc.GetAddressAsync(account, ct).ConfigureAwait(false)).Addresses.Count;
            uint target = Math.Min(wanted, MaxPerAccount);
            while (target > have)
            {
                // monero-wallet-rpc up to v0.18.4.2 refuses more than 64 per call
                // ("Count must be between 1 and 64"): a wallet that had handed out more would
                // otherwise never open again.
                uint batch = Math.Min(target - have, MaxSubaddressesPerCall);
                await rpc.CreateSubaddressesAsync(account, batch, ct).ConfigureAwait(false);
                have += batch;
            }
        }
    }

    /// <summary>The most subaddresses one create_address call may ask for, on every supported
    /// monero-wallet-rpc (the limit rose from 64 to 65536 only in v0.18.4.3).</summary>
    internal const uint MaxSubaddressesPerCall = 64;

    private MoneroRpcClient Rpc => _rpc ?? throw new InvalidOperationException("No wallet is open.");

    public async Task<(decimal balance, decimal unlocked)> GetBalanceAsync(CancellationToken ct = default) =>
        await GetBalanceAsync(0, ct).ConfigureAwait(false);

    public async Task<(decimal balance, decimal unlocked)> GetBalanceAsync(uint account, CancellationToken ct = default)
    {
        GetBalanceResult r = await Rpc.GetBalanceAsync(account, ct).ConfigureAwait(false);
        return (MoneroRpcClient.AtomicToXmr(r.Balance), MoneroRpcClient.AtomicToXmr(r.UnlockedBalance));
    }

    public async Task<string> GetPrimaryAddressAsync(CancellationToken ct = default)
    {
        GetAddressResult r = await Rpc.GetAddressAsync(0, ct).ConfigureAwait(false);
        return r.Address;
    }

    public async Task<string> NewSubaddressAsync(string label, CancellationToken ct = default) =>
        (await NewSubaddressAsync(0, label, ct).ConfigureAwait(false)).address;

    /// <summary>A fresh subaddress in <paramref name="account"/>. Returns its index too: the caller
    /// records the new count so it survives a lock.</summary>
    public async Task<(uint index, string address)> NewSubaddressAsync(uint account, string label, CancellationToken ct = default)
    {
        CreateAddressResult r = await Rpc.CreateSubaddressAsync(account, label ?? string.Empty, ct).ConfigureAwait(false);
        return (r.AddressIndex, r.Address);
    }

    /// <summary>Every address the wallet has handed out in <paramref name="account"/>, index 0 first.</summary>
    public async Task<IReadOnlyList<AddressInfo>> GetAddressesAsync(uint account, CancellationToken ct = default) =>
        (await Rpc.GetAddressAsync(account, ct).ConfigureAwait(false)).Addresses.OrderBy(a => a.AddressIndex).ToList();

    /// <summary>The wallet's accounts with balances (labels come from the profile, not from here).</summary>
    public async Task<GetAccountsResult> GetAccountsAsync(CancellationToken ct = default) =>
        await Rpc.GetAccountsAsync(ct).ConfigureAwait(false);

    /// <summary>A new account; returns its index and main address.</summary>
    public async Task<(uint index, string address)> NewAccountAsync(CancellationToken ct = default)
    {
        CreateAccountResult r = await Rpc.CreateAccountAsync(string.Empty, ct).ConfigureAwait(false);
        return (r.AccountIndex, r.Address);
    }

    /// <summary>Sign <paramref name="message"/> with the wallet's spend key, as its main address.</summary>
    public async Task<string> SignMessageAsync(string message, CancellationToken ct = default) =>
        (await Rpc.SignAsync(message ?? string.Empty, 0, 0, "spend", ct).ConfigureAwait(false)).Signature;

    public async Task<bool> VerifyMessageAsync(string message, string address, string signature, CancellationToken ct = default) =>
        (await Rpc.VerifyAsync(message ?? string.Empty, address.Trim(), signature.Trim(), ct).ConfigureAwait(false)).Good;

    /// <summary>A reserve proof: that this wallet holds at least <paramref name="amount"/> XMR in
    /// <paramref name="account"/>, or (null) everything it holds.</summary>
    public async Task<string> GetReserveProofAsync(decimal? amount, uint account, string message, CancellationToken ct = default) =>
        (await Rpc.GetReserveProofAsync(amount is { } a ? MoneroRpcClient.XmrToAtomic(a) : null, account, message ?? string.Empty, ct).ConfigureAwait(false)).Signature;

    /// <summary>Check someone's reserve proof. Returns (valid, total proven, of which spent).</summary>
    public async Task<(bool good, decimal total, decimal spent)> CheckReserveProofAsync(string address, string message, string signature, CancellationToken ct = default)
    {
        CheckReserveProofResult r = await Rpc.CheckReserveProofAsync(address.Trim(), message ?? string.Empty, signature.Trim(), ct).ConfigureAwait(false);
        return (r.Good, MoneroRpcClient.AtomicToXmr(r.Total), MoneroRpcClient.AtomicToXmr(r.Spent));
    }

    /// <summary>True when the open wallet's node is on this computer: the only node
    /// <see cref="RescanSpentAsync"/> may be used with.</summary>
    public bool NodeIsLocal => DaemonAddress.IsLoopback(_daemon);

    /// <summary>
    /// Re-check which outputs are spent. This sends EVERY key image of the wallet to the node
    /// (wallet2::rescan_spent → /is_key_image_spent), which lets the node recognise each later spend
    /// of this wallet — so it is refused unless the node is on this computer, as monero-wallet-cli
    /// refuses it without a trusted node.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node isn't local.</exception>
    public Task RescanSpentAsync(CancellationToken ct = default)
    {
        if (!NodeIsLocal)
        {
            throw new InvalidOperationException(
                "Re-checking spent outputs sends all of this wallet's key images to the node, so it is only done with a node on this computer.");
        }

        return Rpc.RescanSpentAsync(ct);
    }

    /// <summary>Point the open wallet at another node, keeping what it has scanned. A node on this
    /// computer stays trusted, as monero-wallet-rpc trusts a local node at launch; any other isn't.</summary>
    public async Task SetDaemonAsync(string daemonAddress, CancellationToken ct = default)
    {
        if (!DaemonAddress.IsValid(daemonAddress))
        {
            throw new ArgumentException("Daemon address must be a valid http(s) URL.", nameof(daemonAddress));
        }

        string address = daemonAddress.Trim();
        await Rpc.SetDaemonAsync(address, DaemonAddress.IsLoopback(address), ct).ConfigureAwait(false);
        _daemon = address;
    }

    /// <summary>The open wallet's private keys and seed, for a backup the user asked to see. The
    /// seed is empty for a wallet restored from keys that has none, and the spend key for a
    /// view-only wallet.</summary>
    public async Task<(string mnemonic, string viewKey, string spendKey)> GetKeysAsync(CancellationToken ct = default)
    {
        async Task<string> Key(string type)
        {
            try
            {
                return (await Rpc.QueryKeyAsync(type, ct).ConfigureAwait(false)).Key;
            }
            catch (MoneroRpcClient.MoneroRpcException)
            {
                return string.Empty; // not available for this kind of wallet
            }
        }

        return (await Key("mnemonic").ConfigureAwait(false), await Key("view_key").ConfigureAwait(false), await Key("spend_key").ConfigureAwait(false));
    }

    public async Task<TransferResult> SendAsync(string address, decimal xmr, uint priority, CancellationToken ct = default)
    {
        (string addr, ulong atomic, uint prio) = ValidateSendArgs(address, xmr, priority);
        return await Rpc.TransferAsync(addr, atomic, prio, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the transaction WITHOUT broadcasting it. Returns the exact fee (baked into the signed
    /// tx) plus the tx metadata needed to broadcast it later with <see cref="RelaySendAsync"/>.
    /// Nothing touches the network until relay; discarding the result cancels the send entirely.
    /// </summary>
    public Task<TransferResult> PrepareSendAsync(string address, decimal xmr, uint priority, CancellationToken ct = default) =>
        PrepareSendAsync([(address, xmr)], 0, priority, ct);

    /// <summary>Build ONE transaction paying every destination, spending from <paramref name="account"/>,
    /// without broadcasting it (see the single-destination overload).</summary>
    public async Task<TransferResult> PrepareSendAsync(IReadOnlyList<(string address, decimal xmr)> destinations, uint account, uint priority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        if (destinations.Count is 0 or > MaxDestinations)
        {
            throw new ArgumentException($"A transaction pays between 1 and {MaxDestinations} recipients.", nameof(destinations));
        }

        var atomic = new List<(string address, ulong atomic)>(destinations.Count);
        uint prio = priority;
        foreach ((string address, decimal xmr) in destinations)
        {
            (string addr, ulong amount, prio) = ValidateSendArgs(address, xmr, priority);
            atomic.Add((addr, amount));
        }

        TransferResult r = await Rpc.PrepareTransferAsync(atomic, account, prio, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(r.TxMetadata))
        {
            throw new InvalidOperationException("The wallet backend did not return transaction metadata for the prepared send.");
        }

        return r;
    }

    /// <summary>A Monero transaction has at most 16 outputs, one of them change.</summary>
    public const int MaxDestinations = 15;

    /// <summary>Broadcast a transaction previously built by <see cref="PrepareSendAsync"/>.
    /// Returns the transaction hash.</summary>
    public async Task<string> RelaySendAsync(string txMetadata, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(txMetadata))
        {
            throw new ArgumentException("Transaction metadata is empty.", nameof(txMetadata));
        }

        RelayTxResult r = await Rpc.RelayTxAsync(txMetadata.Trim(), ct).ConfigureAwait(false);
        return r.TxHash;
    }

    /// <summary>
    /// Build transactions sweeping the ENTIRE spendable balance to <paramref name="address"/>
    /// WITHOUT broadcasting (do_not_relay). Same contract as <see cref="PrepareSendAsync"/>:
    /// the exact per-transaction fees come back baked into the signed txs, discarding the result
    /// cancels everything, and each metadata entry is broadcast via <see cref="RelaySendAsync"/>.
    /// A sweep may split into several transactions when the wallet holds many outputs.
    /// </summary>
    public Task<SweepAllResult> PrepareSweepAllAsync(string address, uint priority, CancellationToken ct = default) =>
        PrepareSweepAllAsync(address, 0, priority, ct);

    public async Task<SweepAllResult> PrepareSweepAllAsync(string address, uint account, uint priority, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("Destination address is empty.", nameof(address));
        }

        if (priority > 3)
        {
            priority = 3;
        }

        SweepAllResult r = await Rpc.PrepareSweepAllAsync(address.Trim(), account, priority, ct).ConfigureAwait(false);
        if (r.TxMetadataList.Count == 0
            || r.TxMetadataList.Count != r.AmountList.Count
            || r.TxMetadataList.Count != r.FeeList.Count
            || r.TxMetadataList.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("The wallet backend returned an incomplete prepared sweep.");
        }

        return r;
    }

    private static (string address, ulong atomic, uint priority) ValidateSendArgs(string address, decimal xmr, uint priority)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("Destination address is empty.", nameof(address));
        }

        if (xmr <= 0m)
        {
            throw new ArgumentException("Amount must be greater than zero.", nameof(xmr));
        }

        if (priority > 3)
        {
            priority = 3;
        }

        ulong atomic = MoneroRpcClient.XmrToAtomic(xmr);
        if (atomic == 0)
        {
            throw new ArgumentException("Amount is below the smallest atomic unit.", nameof(xmr));
        }

        return (address.Trim(), atomic, priority);
    }

    public async Task<ulong> GetHeightAsync(CancellationToken ct = default) =>
        (await Rpc.GetHeightAsync(ct).ConfigureAwait(false)).Height;

    /// <summary>Force a synchronous refresh. May be slow; callers should treat timeouts as non-fatal.</summary>
    public async Task RefreshAsync(CancellationToken ct = default) =>
        await Rpc.RefreshAsync(ct).ConfigureAwait(false);

    /// <summary>Transaction private key for an outgoing tx — used to prove a payment on an explorer.</summary>
    /// <exception cref="InvalidOperationException">The backend answered without a key.</exception>
    public async Task<string> GetTxKeyAsync(string txid, CancellationToken ct = default)
    {
        string key = (await Rpc.GetTxKeyAsync(txid.Trim(), ct).ConfigureAwait(false)).TxKey;
        return string.IsNullOrWhiteSpace(key)
            ? throw new InvalidOperationException("The wallet backend returned no key for that transaction.")
            : key;
    }

    /// <summary>Verify a payment given txid + tx key + address. Returns (received atomic, confirmations, inPool).</summary>
    public async Task<(ulong received, ulong confirmations, bool inPool)> CheckTxKeyAsync(
        string txid, string txKey, string address, CancellationToken ct = default)
    {
        CheckTxKeyResult r = await Rpc.CheckTxKeyAsync(txid.Trim(), txKey.Trim(), address.Trim(), ct).ConfigureAwait(false);
        return (r.Received, r.Confirmations, r.InPool);
    }

    public Task<IReadOnlyList<TransferEntry>> GetHistoryAsync(CancellationToken ct = default) => GetHistoryAsync(0, ct);

    /// <summary>The history of one account, newest first.</summary>
    public async Task<IReadOnlyList<TransferEntry>> GetHistoryAsync(uint account, CancellationToken ct = default)
    {
        GetTransfersResult r = await Rpc.GetTransfersAsync(account, ct).ConfigureAwait(false);
        var all = new List<TransferEntry>();
        all.AddRange(r.In);
        all.AddRange(r.Out);
        all.AddRange(r.Pending);
        all.AddRange(r.Pool);
        return all.OrderByDescending(t => t.Timestamp).ToList();
    }

    public async Task CloseAsync()
    {
        try
        {
            _rpc?.Dispose();
        }
        catch { /* ignore */ }
        _rpc = null;
        _daemon = string.Empty;

        MoneroProcessManager? proc = _proc;
        _proc = null;
        if (proc is not null)
        {
            await proc.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
