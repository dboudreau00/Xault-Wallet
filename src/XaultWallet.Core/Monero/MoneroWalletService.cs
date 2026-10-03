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

    public bool IsOpen => _rpc is not null;

    /// <summary>True when the backend wallet-rpc process died underneath an open wallet
    /// (crash, external kill). Distinguishes "backend is gone" from "node is slow" so the
    /// UI can offer a restart instead of surfacing repeated connection errors.</summary>
    public bool BackendExited => _proc?.HasProcessExited == true;

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreachable/odd daemon: fall back to 0 — a full scan later is slow but never loses funds.
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
    public async Task<string> ValidateSeedOpensAsync(WalletSecrets secrets, CancellationToken ct = default)
    {
        await using var proc = new MoneroProcessManager(_walletRpcBinary, _options);
        using MoneroRpcClient rpc = await proc.StartFromSeedAsync(secrets, ct).ConfigureAwait(false);
        GetAddressResult addr = await rpc.GetAddressAsync(0, ct).ConfigureAwait(false);
        try { await rpc.CloseWalletAsync(ct).ConfigureAwait(false); } catch { }
        return addr.Address;
    }

    public async Task OpenAsync(WalletSecrets secrets, CancellationToken ct = default)
    {
        await CloseAsync().ConfigureAwait(false);
        var proc = new MoneroProcessManager(_walletRpcBinary, _options);
        try
        {
            _rpc = await proc.StartFromSeedAsync(secrets, ct).ConfigureAwait(false);
            _proc = proc; // only assign once fully started, so a failed open leaves us cleanly closed
        }
        catch
        {
            await proc.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private MoneroRpcClient Rpc => _rpc ?? throw new InvalidOperationException("No wallet is open.");

    public async Task<(decimal balance, decimal unlocked)> GetBalanceAsync(CancellationToken ct = default)
    {
        GetBalanceResult r = await Rpc.GetBalanceAsync(0, ct).ConfigureAwait(false);
        return (MoneroRpcClient.AtomicToXmr(r.Balance), MoneroRpcClient.AtomicToXmr(r.UnlockedBalance));
    }

    public async Task<string> GetPrimaryAddressAsync(CancellationToken ct = default)
    {
        GetAddressResult r = await Rpc.GetAddressAsync(0, ct).ConfigureAwait(false);
        return r.Address;
    }

    public async Task<string> NewSubaddressAsync(string label, CancellationToken ct = default)
    {
        CreateAddressResult r = await Rpc.CreateSubaddressAsync(0, label, ct).ConfigureAwait(false);
        return r.Address;
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
    public async Task<TransferResult> PrepareSendAsync(string address, decimal xmr, uint priority, CancellationToken ct = default)
    {
        (string addr, ulong atomic, uint prio) = ValidateSendArgs(address, xmr, priority);
        TransferResult r = await Rpc.PrepareTransferAsync(addr, atomic, prio, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(r.TxMetadata))
        {
            throw new InvalidOperationException("The wallet backend did not return transaction metadata for the prepared send.");
        }

        return r;
    }

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
    public async Task<SweepAllResult> PrepareSweepAllAsync(string address, uint priority, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("Destination address is empty.", nameof(address));
        }

        if (priority > 3)
        {
            priority = 3;
        }

        SweepAllResult r = await Rpc.PrepareSweepAllAsync(address.Trim(), priority, ct).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<TransferEntry>> GetHistoryAsync(CancellationToken ct = default)
    {
        GetTransfersResult r = await Rpc.GetTransfersAsync(ct: ct).ConfigureAwait(false);
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

        MoneroProcessManager? proc = _proc;
        _proc = null;
        if (proc is not null)
        {
            await proc.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
