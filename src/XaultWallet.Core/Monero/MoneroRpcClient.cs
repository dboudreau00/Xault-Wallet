using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Thin JSON-RPC 2.0 client for a running monero-wallet-rpc instance.
///
/// The app's wallet-rpc child requires HTTP Digest auth with per-session random credentials
/// (see <see cref="MoneroProcessManager"/>). An unauthenticated child would accept requests from
/// anything that can reach loopback — including a web page (a cross-origin "simple" POST, or DNS
/// rebinding, which can READ responses such as query_key → the seed).
///
/// Amounts are in atomic units: 1 XMR = 1e12 atomic units.
/// </summary>
public sealed class MoneroRpcClient : IDisposable
{
    public const ulong AtomicUnitsPerXmr = 1_000_000_000_000;

    private readonly HttpClient _http;
    private int _id;

    public MoneroRpcClient(Uri endpoint, string? rpcUser = null, string? rpcPassword = null)
        : this(endpoint, string.IsNullOrEmpty(rpcUser) ? null : new NetworkCredential(rpcUser, rpcPassword))
    {
    }

    public MoneroRpcClient(Uri endpoint, NetworkCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var handler = new SocketsHttpHandler
        {
            // Seed-bearing loopback traffic must never be routed through a system/env proxy.
            UseProxy = false,
            AllowAutoRedirect = false,
        };

        if (credential is not null)
        {
            // Digest ONLY: a server that answers with a Basic challenge (i.e. not monero-wallet-rpc)
            // must never receive the password in cleartext.
            handler.Credentials = new CredentialCache { { endpoint, "Digest", credential } };
            handler.PreAuthenticate = true;
        }

        _http = new HttpClient(handler)
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    // monero-wallet-rpc's JSON-RPC parser rejects a "params": null field with -32600 Invalid
    // Request; the field must be omitted when there are no parameters. Params objects are
    // serialized with null properties dropped.
    private static readonly JsonSerializerOptions RpcJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class RpcResponse<T>
    {
        [JsonPropertyName("result")] public T? Result { get; set; }
        [JsonPropertyName("error")] public RpcError? Error { get; set; }
    }

    private sealed class RpcError
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
    }

    /// <summary>An error reported by (or while talking to) monero-wallet-rpc. <see cref="Exception.Message"/>
    /// names the method and the backend's own error text and is safe to show and to log;
    /// <see cref="Diagnostics"/> holds the redacted request/response for debugging.</summary>
    public sealed class MoneroRpcException(int code, string message, string? diagnostics = null)
        : Exception($"monero-wallet-rpc error {code}: {message}")
    {
        public int Code { get; } = code;

        /// <summary>
        /// Redacted request/response excerpt. Seeds, passwords and keys are already masked
        /// (<see cref="SecretRedactor"/>), but it can still carry destination addresses, amounts and
        /// restore heights — enough to tie a persistent log line to ONE of the vault's wallets. So it
        /// is kept out of <see cref="Exception.Message"/> and the app never writes it to its log.
        /// </summary>
        public string Diagnostics { get; } = diagnostics ?? string.Empty;
    }

    public async Task<T> CallAsync<T>(string method, object? @params = null, CancellationToken ct = default)
    {
        // Build the JSON-RPC envelope by hand so there is zero ambiguity about what goes on the
        // wire (record + attribute serialization was producing requests monero-wallet-rpc rejected).
        string id = Interlocked.Increment(ref _id).ToString(CultureInfo.InvariantCulture);
        string paramsJson = @params is null ? "" : "," + "\"params\":" + JsonSerializer.Serialize(@params, RpcJson);
        string payload = $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"{method}\"{paramsJson}}}";

        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync("/json_rpc", content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw; // connection-level; callers/readiness logic handle this
        }

        using (resp)
        {
            string raw = "";
            try { raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }

            // Secrets carried in the request (seed, offset, passwords) or echoed in a response must
            // never reach anything that is displayed or logged: redact both directions.
            string Diagnostics() => $"Sent: {SecretRedactor.Redact(payload)} Got: {Trim(SecretRedactor.Redact(raw))}";

            if (!resp.IsSuccessStatusCode)
            {
                throw new MoneroRpcException((int)resp.StatusCode,
                    $"{method}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", Diagnostics());
            }

            RpcResponse<T>? body;
            try
            {
                body = JsonSerializer.Deserialize<RpcResponse<T>>(raw);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new MoneroRpcException(-32700, $"{method}: the backend returned malformed JSON", Diagnostics());
            }

            if (body is null)
            {
                throw new MoneroRpcException(-32603, $"{method}: empty response", Diagnostics());
            }

            if (body.Error is { } err)
            {
                throw new MoneroRpcException(err.Code, $"{method}: {err.Message}", Diagnostics());
            }

            if (body.Result is null)
            {
                throw new MoneroRpcException(-32603, $"{method}: no result", Diagnostics());
            }

            return body.Result;
        }
    }

    private static string Trim(string s) => string.IsNullOrEmpty(s) ? "(empty)" : s.Substring(0, Math.Min(300, s.Length));

    // ---- Typed convenience wrappers for the handful of methods the UI needs ----

    public Task<GetBalanceResult> GetBalanceAsync(uint accountIndex = 0, CancellationToken ct = default) =>
        CallAsync<GetBalanceResult>("get_balance", new { account_index = accountIndex }, ct);

    /// <summary>Every address of an account the wallet has created (index 0 is the account's main
    /// address), with labels and whether each has received anything.</summary>
    public Task<GetAddressResult> GetAddressAsync(uint accountIndex = 0, CancellationToken ct = default) =>
        CallAsync<GetAddressResult>("get_address", new { account_index = accountIndex }, ct);

    public Task<CreateAddressResult> CreateSubaddressAsync(uint accountIndex = 0, string label = "", CancellationToken ct = default) =>
        CallAsync<CreateAddressResult>("create_address", new { account_index = accountIndex, label }, ct);

    /// <summary>Create <paramref name="count"/> subaddresses at once (restoring how many a wallet had
    /// handed out before it was locked).</summary>
    public Task<CreateAddressResult> CreateSubaddressesAsync(uint accountIndex, uint count, CancellationToken ct = default) =>
        CallAsync<CreateAddressResult>("create_address", new { account_index = accountIndex, count, label = "" }, ct);

    public Task LabelAddressAsync(uint accountIndex, uint addressIndex, string label, CancellationToken ct = default) =>
        CallAsync<JsonElement>("label_address", new { index = new { major = accountIndex, minor = addressIndex }, label }, ct);

    /// <summary>Accounts with their balances and labels.</summary>
    public Task<GetAccountsResult> GetAccountsAsync(CancellationToken ct = default) =>
        CallAsync<GetAccountsResult>("get_accounts", new { }, ct);

    public Task<CreateAccountResult> CreateAccountAsync(string label = "", CancellationToken ct = default) =>
        CallAsync<CreateAccountResult>("create_account", new { label }, ct);

    public Task LabelAccountAsync(uint accountIndex, string label, CancellationToken ct = default) =>
        CallAsync<JsonElement>("label_account", new { account_index = accountIndex, label }, ct);

    /// <summary>Sign a message with the wallet's spend key (proves you control the address), or with
    /// <paramref name="signatureType"/> "view", its view key.</summary>
    public Task<SignResult> SignAsync(string data, uint accountIndex = 0, uint addressIndex = 0, string signatureType = "spend", CancellationToken ct = default) =>
        CallAsync<SignResult>("sign", new { data, account_index = accountIndex, address_index = addressIndex, signature_type = signatureType }, ct);

    public Task<VerifyResult> VerifyAsync(string data, string address, string signature, CancellationToken ct = default) =>
        CallAsync<VerifyResult>("verify", new { data, address, signature }, ct);

    /// <summary>Prove the wallet holds at least <paramref name="amount"/> (or, with null, everything in
    /// <paramref name="accountIndex"/>... all accounts) without revealing more.</summary>
    public Task<SignResult> GetReserveProofAsync(ulong? amount, uint accountIndex, string message, CancellationToken ct = default) =>
        CallAsync<SignResult>("get_reserve_proof", amount is { } a
            ? new { all = false, account_index = accountIndex, amount = a, message }
            : (object)new { all = true, message }, ct);

    public Task<CheckReserveProofResult> CheckReserveProofAsync(string address, string message, string signature, CancellationToken ct = default) =>
        CallAsync<CheckReserveProofResult>("check_reserve_proof", new { address, message, signature }, ct);

    /// <summary>One transaction of this wallet by id (all accounts).</summary>
    public Task<GetTransferByTxidResult> GetTransferByTxidAsync(string txid, CancellationToken ct = default) =>
        CallAsync<GetTransferByTxidResult>("get_transfer_by_txid", new { txid }, ct);

    /// <summary>Re-check which outputs are spent (fixes a balance confused by a bad node).</summary>
    public Task RescanSpentAsync(CancellationToken ct = default) =>
        CallAsync<JsonElement>("rescan_spent", null, ct);

    /// <summary>Point the open wallet at another node without closing it.</summary>
    /// <summary>Switch the open wallet's node. <paramref name="trusted"/> enables commands that hand
    /// the node more than it would otherwise learn (rescan_spent sends every key image).</summary>
    // An https:// node gets ssl_support "enabled", as at launch (see MoneroProcessManager.DaemonSslMode).
    public Task SetDaemonAsync(string address, bool trusted, CancellationToken ct = default) =>
        CallAsync<JsonElement>("set_daemon", new { address, trusted, ssl_support = MoneroProcessManager.DaemonSslMode(address) }, ct);

    /// <summary>The account's unspent outputs ("coins"), each with its key image and frozen flag.
    /// A watch-only wallet lists them with empty key images (computing one takes the spend key).</summary>
    public Task<IncomingTransfersResult> GetUnspentOutputsAsync(uint accountIndex, CancellationToken ct = default) =>
        CallAsync<IncomingTransfersResult>("incoming_transfers", new { transfer_type = "available", account_index = accountIndex }, ct);

    /// <summary>Never spend this output until <see cref="ThawAsync"/>: transfer and sweep_all skip it.</summary>
    public Task FreezeAsync(string keyImage, CancellationToken ct = default) =>
        CallAsync<JsonElement>("freeze", new { key_image = keyImage }, ct);

    public Task ThawAsync(string keyImage, CancellationToken ct = default) =>
        CallAsync<JsonElement>("thaw", new { key_image = keyImage }, ct);

    public Task<GetHeightResult> GetHeightAsync(CancellationToken ct = default) =>
        CallAsync<GetHeightResult>("get_height", null, ct);

    /// <summary>Works with or without an open wallet; used as the process-readiness probe.</summary>
    public Task<GetVersionResult> GetVersionAsync(CancellationToken ct = default) =>
        CallAsync<GetVersionResult>("get_version", null, ct);

    public Task RefreshAsync(CancellationToken ct = default) =>
        CallAsync<JsonElement>("refresh", null, ct);

    public Task<TransferResult> TransferAsync(string address, ulong atomicAmount, uint priority = 0, CancellationToken ct = default) =>
        CallAsync<TransferResult>("transfer", new
        {
            destinations = new[] { new { amount = atomicAmount, address } },
            account_index = 0u,
            priority,
            get_tx_key = true,
        }, ct);

    /// <summary>Build a transfer WITHOUT broadcasting it (do_not_relay). Returns the exact fee and
    /// the signed tx metadata; nothing touches the network until <see cref="RelayTxAsync"/> is
    /// called with that metadata. Used to show the real fee in the send-confirm dialog.</summary>
    public Task<TransferResult> PrepareTransferAsync(string address, ulong atomicAmount, uint priority = 0, CancellationToken ct = default) =>
        PrepareTransferAsync([(address, atomicAmount)], 0, priority, ct);

    /// <summary>Like <see cref="PrepareTransferAsync(string, ulong, uint, CancellationToken)"/>, to
    /// one or more destinations in ONE transaction, spending from <paramref name="accountIndex"/>.</summary>
    public Task<TransferResult> PrepareTransferAsync(IReadOnlyList<(string address, ulong atomic)> destinations, uint accountIndex, uint priority, CancellationToken ct = default) =>
        CallAsync<TransferResult>("transfer", new
        {
            destinations = destinations.Select(d => new { amount = d.atomic, address = d.address }).ToArray(),
            account_index = accountIndex,
            priority,
            get_tx_key = true,
            do_not_relay = true,
            get_tx_metadata = true,
        }, ct);

    /// <summary>Broadcast a transaction previously built by <see cref="PrepareTransferAsync"/>.
    /// The fee cannot change between prepare and relay — it is baked into the signed tx.</summary>
    public Task<RelayTxResult> RelayTxAsync(string txMetadata, CancellationToken ct = default) =>
        CallAsync<RelayTxResult>("relay_tx", new { hex = txMetadata }, ct);

    /// <summary>Build transactions sweeping the ENTIRE spendable balance to one address WITHOUT
    /// broadcasting (do_not_relay). A sweep can span several transactions when the wallet holds
    /// many outputs; each entry in the result lists gets its own relay. Same contract as
    /// <see cref="PrepareTransferAsync"/>: discarding the result cancels everything.</summary>
    public Task<SweepAllResult> PrepareSweepAllAsync(string address, uint priority = 0, CancellationToken ct = default) =>
        PrepareSweepAllAsync(address, 0, priority, ct);

    public Task<SweepAllResult> PrepareSweepAllAsync(string address, uint accountIndex, uint priority, CancellationToken ct = default) =>
        CallAsync<SweepAllResult>("sweep_all", new
        {
            address,
            account_index = accountIndex,
            priority,
            get_tx_keys = true,
            do_not_relay = true,
            get_tx_metadata = true,
        }, ct);

    public Task<GetTransfersResult> GetTransfersAsync(bool @in = true, bool @out = true, bool pending = true, CancellationToken ct = default) =>
        CallAsync<GetTransfersResult>("get_transfers", new { @in, @out, pending, pool = pending }, ct);

    /// <summary>History of one account (incoming, outgoing, pending and mempool).</summary>
    public Task<GetTransfersResult> GetTransfersAsync(uint accountIndex, CancellationToken ct = default) =>
        CallAsync<GetTransfersResult>("get_transfers", new { @in = true, @out = true, pending = true, pool = true, failed = false, account_index = accountIndex }, ct);

    // ---- Payment proof (transaction key) ----

    /// <summary>Get the transaction private key for an outgoing tx. Safe to share to prove a
    /// specific payment (it does NOT let anyone spend your funds).</summary>
    public Task<GetTxKeyResult> GetTxKeyAsync(string txid, CancellationToken ct = default) =>
        CallAsync<GetTxKeyResult>("get_tx_key", new { txid }, ct);

    /// <summary>Verify a payment: given txid + tx key + destination address, returns how much
    /// that address received and how many confirmations it has.</summary>
    public Task<CheckTxKeyResult> CheckTxKeyAsync(string txid, string txKey, string address, CancellationToken ct = default) =>
        CallAsync<CheckTxKeyResult>("check_tx_key", new { txid, tx_key = txKey, address }, ct);

    // ---- Wallet lifecycle (used by seed generation) ----

    /// <summary>Create a brand-new deterministic wallet. The RPC server must have been started
    /// with --wallet-dir and no wallet open. The wallet is left open afterwards.</summary>
    public Task CreateWalletAsync(string filename, string password, string language = "English", CancellationToken ct = default) =>
        CallAsync<JsonElement>("create_wallet", new { filename, password, language }, ct);

    /// <summary>Restore a wallet from a 25-word seed on the already-running RPC server. This does
    /// NOT require the daemon (scanning happens in the background afterward), so it decouples
    /// wallet-open from the daemon connection.</summary>
    public Task RestoreDeterministicWalletAsync(
        string filename, string password, string seed, ulong restoreHeight,
        string seedOffset = "", CancellationToken ct = default) =>
        CallAsync<JsonElement>("restore_deterministic_wallet", new
        {
            filename,
            password,
            seed,
            restore_height = restoreHeight,
            seed_offset = seedOffset ?? "",
            autosave_current = true,
        }, ct);

    /// <summary>Restore a wallet from its keys. With an empty <paramref name="spendKey"/> it is a
    /// view-only (watch) wallet. Like restore_deterministic_wallet, it needs no daemon.</summary>
    public Task<GenerateFromKeysResult> GenerateFromKeysAsync(
        string filename, string password, string address, string viewKey, string spendKey, ulong restoreHeight, CancellationToken ct = default) =>
        CallAsync<GenerateFromKeysResult>("generate_from_keys", new
        {
            filename,
            password,
            address,
            viewkey = viewKey,
            spendkey = spendKey ?? "",
            restore_height = restoreHeight,
            autosave_current = true,
        }, ct);

    /// <summary>Retrieve a key from the currently open wallet. key_type is "mnemonic", "view_key" or "spend_key".</summary>
    public Task<QueryKeyResult> QueryKeyAsync(string keyType, CancellationToken ct = default) =>
        CallAsync<QueryKeyResult>("query_key", new { key_type = keyType }, ct);

    public Task CloseWalletAsync(CancellationToken ct = default) =>
        CallAsync<JsonElement>("close_wallet", null, ct);

    /// <summary>Open a wallet file from --wallet-dir. Loading checks that the wallet's secret keys
    /// belong to its address (wallet2::load_keys), which generating one from keys does not.</summary>
    public Task OpenWalletAsync(string filename, string password, CancellationToken ct = default) =>
        CallAsync<JsonElement>("open_wallet", new { filename, password }, ct);

    public static decimal AtomicToXmr(ulong atomic) => atomic / (decimal)AtomicUnitsPerXmr;

    /// <summary>Upper bound for amount input: at or below ulong.MaxValue in atomic units
    /// (18,446,744.07... XMR) so the guarded range never reaches the raw decimal→ulong
    /// OverflowException, and comfortably above the ~18.4M real emission — anything bigger
    /// is a typo.</summary>
    public const decimal MaxXmrAmount = 18_446_744m;

    public static ulong XmrToAtomic(decimal xmr)
    {
        if (xmr < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(xmr), "Amount cannot be negative.");
        }

        if (xmr > MaxXmrAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(xmr), "Amount exceeds the total Monero supply — check for a typo.");
        }

        return (ulong)decimal.Round(xmr * AtomicUnitsPerXmr, 0);
    }

    public void Dispose() => _http.Dispose();
}

// ---- Result DTOs ----

public sealed class GetBalanceResult
{
    [JsonPropertyName("balance")] public ulong Balance { get; set; }
    [JsonPropertyName("unlocked_balance")] public ulong UnlockedBalance { get; set; }
    [JsonPropertyName("blocks_to_unlock")] public uint BlocksToUnlock { get; set; }
}

public sealed class GetAddressResult
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("addresses")] public List<AddressInfo> Addresses { get; set; } = new();
}

public sealed class AddressInfo
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("address_index")] public uint AddressIndex { get; set; }

    /// <summary>Has received a payment.</summary>
    [JsonPropertyName("used")] public bool Used { get; set; }
}

public sealed class CreateAddressResult
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("address_index")] public uint AddressIndex { get; set; }
    [JsonPropertyName("addresses")] public List<string> Addresses { get; set; } = new();
    [JsonPropertyName("address_indices")] public List<uint> AddressIndices { get; set; } = new();
}

public sealed class GetAccountsResult
{
    [JsonPropertyName("total_balance")] public ulong TotalBalance { get; set; }
    [JsonPropertyName("total_unlocked_balance")] public ulong TotalUnlockedBalance { get; set; }
    [JsonPropertyName("subaddress_accounts")] public List<AccountInfo> Accounts { get; set; } = new();
}

public sealed class AccountInfo
{
    [JsonPropertyName("account_index")] public uint AccountIndex { get; set; }
    [JsonPropertyName("base_address")] public string BaseAddress { get; set; } = "";
    [JsonPropertyName("balance")] public ulong Balance { get; set; }
    [JsonPropertyName("unlocked_balance")] public ulong UnlockedBalance { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";
}

public sealed class CreateAccountResult
{
    [JsonPropertyName("account_index")] public uint AccountIndex { get; set; }
    [JsonPropertyName("address")] public string Address { get; set; } = "";
}

public sealed class SignResult
{
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}

public sealed class VerifyResult
{
    [JsonPropertyName("good")] public bool Good { get; set; }

    /// <summary>"spend" or "view": which of the address's keys the signature was made with.</summary>
    [JsonPropertyName("signature_type")] public string SignatureType { get; set; } = "";
}

public sealed class CheckReserveProofResult
{
    [JsonPropertyName("good")] public bool Good { get; set; }
    [JsonPropertyName("total")] public ulong Total { get; set; }
    [JsonPropertyName("spent")] public ulong Spent { get; set; }
}

public sealed class GetTransferByTxidResult
{
    [JsonPropertyName("transfer")] public TransferEntry Transfer { get; set; } = new();
    [JsonPropertyName("transfers")] public List<TransferEntry> Transfers { get; set; } = new();
}

public sealed class GenerateFromKeysResult
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("info")] public string Info { get; set; } = "";
}

public sealed class GetHeightResult
{
    [JsonPropertyName("height")] public ulong Height { get; set; }
}

public sealed class QueryKeyResult
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
}

/// <summary>get_tx_key answers with "tx_key" — NOT "key" like query_key. Reading it through
/// <see cref="QueryKeyResult"/> silently produced an empty key for every transaction.</summary>
public sealed class GetTxKeyResult
{
    [JsonPropertyName("tx_key")] public string TxKey { get; set; } = "";
}

public sealed class GetVersionResult
{
    [JsonPropertyName("version")] public uint Version { get; set; }
}

public sealed class CheckTxKeyResult
{
    [JsonPropertyName("received")] public ulong Received { get; set; }
    [JsonPropertyName("confirmations")] public ulong Confirmations { get; set; }
    [JsonPropertyName("in_pool")] public bool InPool { get; set; }
}

public sealed class TransferResult
{
    [JsonPropertyName("tx_hash")] public string TxHash { get; set; } = "";
    [JsonPropertyName("tx_key")] public string TxKey { get; set; } = "";
    [JsonPropertyName("amount")] public ulong Amount { get; set; }
    [JsonPropertyName("fee")] public ulong Fee { get; set; }

    /// <summary>Signed-tx metadata (only when requested via get_tx_metadata). Holding it lets the
    /// caller broadcast later with relay_tx. Anyone with this blob can broadcast the tx, so it
    /// stays in memory and is redacted from any error/log output.</summary>
    [JsonPropertyName("tx_metadata")] public string TxMetadata { get; set; } = "";
}

public sealed class RelayTxResult
{
    [JsonPropertyName("tx_hash")] public string TxHash { get; set; } = "";
}

/// <summary>Result of sweep_all: parallel lists, one entry per transaction the sweep was split
/// into. With do_not_relay + get_tx_metadata, each metadata blob is broadcast separately.</summary>
public sealed class SweepAllResult
{
    [JsonPropertyName("tx_hash_list")] public List<string> TxHashList { get; set; } = new();
    [JsonPropertyName("tx_key_list")] public List<string> TxKeyList { get; set; } = new();
    [JsonPropertyName("amount_list")] public List<ulong> AmountList { get; set; } = new();
    [JsonPropertyName("fee_list")] public List<ulong> FeeList { get; set; } = new();
    [JsonPropertyName("tx_metadata_list")] public List<string> TxMetadataList { get; set; } = new();
}

public sealed class GetTransfersResult
{
    [JsonPropertyName("in")] public List<TransferEntry> In { get; set; } = new();
    [JsonPropertyName("out")] public List<TransferEntry> Out { get; set; } = new();
    [JsonPropertyName("pending")] public List<TransferEntry> Pending { get; set; } = new();
    [JsonPropertyName("pool")] public List<TransferEntry> Pool { get; set; } = new();
}

public sealed class TransferEntry
{
    [JsonPropertyName("txid")] public string TxId { get; set; } = "";
    [JsonPropertyName("amount")] public ulong Amount { get; set; }
    [JsonPropertyName("fee")] public ulong Fee { get; set; }
    [JsonPropertyName("height")] public ulong Height { get; set; }
    [JsonPropertyName("timestamp")] public ulong Timestamp { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";

    /// <summary>Which subaddress received it (incoming) or which account spent it (outgoing).</summary>
    [JsonPropertyName("subaddr_index")] public SubaddressIndex SubaddrIndex { get; set; } = new();

    /// <summary>Where an outgoing payment went (known only to the wallet that sent it).</summary>
    [JsonPropertyName("destinations")] public List<TransferDestination>? Destinations { get; set; }

    [JsonPropertyName("confirmations")] public ulong Confirmations { get; set; }
    [JsonPropertyName("double_spend_seen")] public bool DoubleSpendSeen { get; set; }

    /// <summary>Local date/time of the transaction for display (empty if not yet timestamped).</summary>
    [JsonIgnore]
    public string Date => Timestamp == 0
        ? ""
        : DateTimeOffset.FromUnixTimeSeconds((long)Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>incoming_transfers answers {} (no "transfers") when there are none.</summary>
public sealed class IncomingTransfersResult
{
    [JsonPropertyName("transfers")] public List<OwnedOutput> Transfers { get; set; } = new();
}

/// <summary>One output the wallet owns: a "coin", in coin-control terms.</summary>
public sealed class OwnedOutput
{
    [JsonPropertyName("amount")] public ulong Amount { get; set; }
    [JsonPropertyName("spent")] public bool Spent { get; set; }
    [JsonPropertyName("frozen")] public bool Frozen { get; set; }
    [JsonPropertyName("unlocked")] public bool Unlocked { get; set; }
    [JsonPropertyName("key_image")] public string KeyImage { get; set; } = "";
    [JsonPropertyName("tx_hash")] public string TxHash { get; set; } = "";
    [JsonPropertyName("block_height")] public ulong BlockHeight { get; set; }
    [JsonPropertyName("global_index")] public ulong GlobalIndex { get; set; }

    /// <summary>Which subaddress received it.</summary>
    [JsonPropertyName("subaddr_index")] public SubaddressIndex SubaddrIndex { get; set; } = new();
}

public sealed class SubaddressIndex
{
    [JsonPropertyName("major")] public uint Major { get; set; }
    [JsonPropertyName("minor")] public uint Minor { get; set; }
}

public sealed class TransferDestination
{
    [JsonPropertyName("amount")] public ulong Amount { get; set; }
    [JsonPropertyName("address")] public string Address { get; set; } = "";
}
