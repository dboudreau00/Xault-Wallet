using System.Text.Json.Nodes;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Redacts the values of known-secret keys from a JSON string so request/response bodies can be
/// safely embedded in log lines and error messages. Hard rule #7 (seeds, passwords, and keys must
/// NEVER reach the log file) is non-negotiable, and RPC error messages that echo the request body
/// would otherwise carry the 25-word seed and the seed-offset passphrase straight to disk and to
/// the screen.
///
/// The redaction is structural — it parses the JSON and blanks secret values wherever they appear,
/// so it is not fooled by key ordering or whitespace. Input that is not JSON is returned unchanged:
/// the RPC's non-JSON error bodies (HTTP/HTML error pages) do not carry our secrets, and keeping
/// them intact preserves their debugging value.
/// </summary>
public static class SecretRedactor
{
    private const string Mask = "***";

    /// <summary>
    /// Keys whose values are secret in monero-wallet-rpc requests/responses: the seed and its
    /// offset, wallet passwords, and any raw key material. Matched case-insensitively.
    /// </summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "seed", "seed_offset", "password", "key", "spend_key", "view_key", "tx_key", "mnemonic",
    };

    public static string Redact(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json ?? string.Empty;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return json; // not JSON => not one of our secret-bearing RPC bodies
        }

        if (root is null)
        {
            return json;
        }

        try
        {
            RedactNode(root);
            return root.ToJsonString();
        }
        catch
        {
            // If the walk/serialize fails for any reason, fail safe: reveal nothing.
            return "(redacted)";
        }
    }

    private static void RedactNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> kv in obj.ToList())
                {
                    if (SecretKeys.Contains(kv.Key))
                    {
                        obj[kv.Key] = Mask;
                    }
                    else if (kv.Value is not null)
                    {
                        RedactNode(kv.Value);
                    }
                }

                break;

            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    if (item is not null)
                    {
                        RedactNode(item);
                    }
                }

                break;
        }
    }
}
