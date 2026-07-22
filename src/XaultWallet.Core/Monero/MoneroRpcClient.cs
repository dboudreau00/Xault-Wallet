using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Thin JSON-RPC 2.0 client for a running monero-wallet-rpc instance.
/// Authentication uses HTTP Digest (monero-wallet-rpc's default when launched
/// with --rpc-login), which HttpClient negotiates automatically from the
/// supplied NetworkCredential.
///
/// Amounts are in atomic units: 1 XMR = 1e12 atomic units.
/// </summary>
public sealed class MoneroRpcClient : IDisposable
{
    public const ulong AtomicUnitsPerXmr = 1_000_000_000_000;

    private readonly HttpClient _http;
    private int _id;

    public MoneroRpcClient(Uri endpoint, string? rpcUser = null, string? rpcPassword = null)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(rpcUser))
        {
            // Only relevant if RPC auth is enabled. Note: digest auth + a POST body can fail to
            // replay after the 401 challenge, which is why the ephemeral local instance is
            // launched with --disable-rpc-login instead.
            handler.Credentials = new NetworkCredential(rpcUser, rpcPassword);
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

    public sealed class MoneroRpcException(int code, string message)
        : Exception($"monero-wallet-rpc error {code}: {message}")
    {
        public int Code { get; } = code;
    }

    public async Task<T> CallAsync<T>(string method, object? @params = null, CancellationToken ct = default)
    {
        // Build the JSON-RPC envelope by hand so there is zero ambiguity about what goes on the
        // wire (record + attribute serialization was producing requests monero-wallet-rpc rejected).
        string id = Interlocked.Increment(ref _id).ToString();
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

            if (!resp.IsSuccessStatusCode)
            {
                throw new MoneroRpcException((int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Sent: {payload}. Got: {Trim(raw)}");
            }

            RpcResponse<T>? body;
            try
            {
                body = JsonSerializer.Deserialize<RpcResponse<T>>(raw);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new MoneroRpcException(-32700, $"Bad response JSON: {ex.Message}. Sent: {payload}. Got: {Trim(raw)}");
            }

            if (body is null)
            {
                throw new MoneroRpcException(-32603, $"Empty response. Sent: {payload}. Got: {Trim(raw)}");
            }

            if (body.Error is { } err)
            {
                throw new MoneroRpcException(err.Code, $"{err.Message}. Sent: {payload}");
            }

            if (body.Result is null)
            {
                throw new MoneroRpcException(-32603, $"No result for '{method}'. Got: {Trim(raw)}");
            }

            return body.Result;
        }
    }

    private static string Trim(string s) => string.IsNullOrEmpty(s) ? "(empty)" : s.Substring(0, Math.Min(300, s.Length));

    // ---- Typed convenience wrappers for the handful of methods the UI needs ----

    public Task<GetBalanceResult> GetBalanceAsync(uint accountIndex = 0, CancellationToken ct = default) =>
        CallAsync<GetBalanceResult>("get_balance", new { account_index = accountIndex }, ct);

    public Task<GetAddressResult> GetAddressAsync(uint accountIndex = 0, CancellationToken ct = default) =>
        CallAsync<GetAddressResult>("get_address", new { account_index = accountIndex }, ct);

    public Task<CreateAddressResult> CreateSubaddressAsync(uint accountIndex = 0, string label = "", CancellationToken ct = default) =>
        CallAsync<CreateAddressResult>("create_address", new { account_index = accountIndex, label }, ct);

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

    public Task<GetTransfersResult> GetTransfersAsync(bool @in = true, bool @out = true, bool pending = true, CancellationToken ct = default) =>
        CallAsync<GetTransfersResult>("get_transfers", new { @in, @out, pending, pool = pending }, ct);

    // ---- Payment proof (transaction key) ----

    /// <summary>Get the transaction private key for an outgoing tx. Safe to share to prove a
    /// specific payment (it does NOT let anyone spend your funds).</summary>
    public Task<QueryKeyResult> GetTxKeyAsync(string txid, CancellationToken ct = default) =>
        CallAsync<QueryKeyResult>("get_tx_key", new { txid }, ct);

    /// <summary>Verify a payment: given txid + tx key + destination address, returns how much
    /// that address received and how many confirmations it has.</summary>
    public Task<CheckTxKeyResult> CheckTxKeyAsync(string txid, string txKey, string address, CancellationToken ct = default) =>
        CallAsync<CheckTxKeyResult>("check_tx_key", new { txid, tx_key = txKey, address }, ct);

    // ---- Wallet lifecycle (used by seed generation) ----

    /// <summary>Create a brand-new deterministic wallet. The RPC server must have been started
    /// with --wallet-dir and no wallet open. The wallet is left open afterwards.</summary>
    public Task CreateWalletAsync(string filename, string password, string language = "English", CancellationToken ct = default) =>
        CallAsync<JsonElement>("create_wallet", new { filename, password, language }, ct);

    /// <summary>Retrieve a key from the currently open wallet. key_type is "mnemonic", "view_key" or "spend_key".</summary>
    public Task<QueryKeyResult> QueryKeyAsync(string keyType, CancellationToken ct = default) =>
        CallAsync<QueryKeyResult>("query_key", new { key_type = keyType }, ct);

    public Task CloseWalletAsync(CancellationToken ct = default) =>
        CallAsync<JsonElement>("close_wallet", null, ct);

    public static decimal AtomicToXmr(ulong atomic) => atomic / (decimal)AtomicUnitsPerXmr;

    public static ulong XmrToAtomic(decimal xmr) => (ulong)decimal.Round(xmr * AtomicUnitsPerXmr, 0);

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
}

public sealed class CreateAddressResult
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("address_index")] public uint AddressIndex { get; set; }
}

public sealed class GetHeightResult
{
    [JsonPropertyName("height")] public ulong Height { get; set; }
}

public sealed class QueryKeyResult
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
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

    /// <summary>Local date/time of the transaction for display (empty if not yet timestamped).</summary>
    [JsonIgnore]
    public string Date => Timestamp == 0
        ? ""
        : DateTimeOffset.FromUnixTimeSeconds((long)Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
