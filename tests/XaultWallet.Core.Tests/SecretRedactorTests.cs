using XaultWallet.Core.Monero;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// The redactor is the last line of defence for hard rule #7: it must strip the seed and the
/// seed-offset passphrase out of any JSON that could reach the log file or the on-screen status.
/// </summary>
public class SecretRedactorTests
{
    [Fact]
    public void Redacts_The_Restore_Wallet_Payload_Seed_And_Offset()
    {
        // This is the exact request that leaked the seed + offset via RPC error messages.
        string payload =
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"restore_deterministic_wallet\"," +
            "\"params\":{\"filename\":\"w\",\"password\":\"ephemeral-pw\"," +
            "\"seed\":\"forbid gawk aztec ... twenty five words\"," +
            "\"restore_height\":123,\"seed_offset\":\"my secret passphrase\",\"autosave_current\":true}}";

        string red = SecretRedactor.Redact(payload);

        Assert.DoesNotContain("twenty five words", red);
        Assert.DoesNotContain("my secret passphrase", red);
        Assert.DoesNotContain("ephemeral-pw", red);
        // Non-secret fields survive so the message is still useful for debugging.
        Assert.Contains("restore_deterministic_wallet", red);
        Assert.Contains("restore_height", red);
        Assert.Contains("123", red);
    }

    [Fact]
    public void Redacts_QueryKey_Response_Key()
    {
        // A response body can also carry a secret (query_key / get_tx_key return "key").
        string resp = "{\"id\":\"1\",\"jsonrpc\":\"2.0\",\"result\":{\"key\":\"deadbeefseedmaterial\"}}";
        string red = SecretRedactor.Redact(resp);
        Assert.DoesNotContain("deadbeefseedmaterial", red);
        Assert.Contains("result", red);
    }

    [Fact]
    public void Non_Json_Is_Returned_Unchanged()
    {
        // HTTP/HTML error bodies aren't secret-bearing RPC JSON — keep them for debugging.
        const string html = "<html><body>502 Bad Gateway</body></html>";
        Assert.Equal(html, SecretRedactor.Redact(html));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_Or_Empty_Is_Safe(string? input)
    {
        Assert.Equal(string.Empty, SecretRedactor.Redact(input));
    }

    [Fact]
    public void Redacts_Signed_Tx_Blobs()
    {
        // relay_tx carries the signed tx as "hex"; transfer responses can carry "tx_metadata".
        // Anyone with these blobs can broadcast the tx, so they never reach an error message.
        string relayReq = "{\"jsonrpc\":\"2.0\",\"id\":\"3\",\"method\":\"relay_tx\",\"params\":{\"hex\":\"deadbeefcafe0123\"}}";
        string red = SecretRedactor.Redact(relayReq);
        Assert.DoesNotContain("deadbeefcafe0123", red);
        Assert.Contains("relay_tx", red);

        string prepResp = "{\"id\":\"4\",\"jsonrpc\":\"2.0\",\"result\":{\"fee\":123,\"tx_metadata\":\"aabbccdd\",\"tx_hash\":\"hash1\"}}";
        string red2 = SecretRedactor.Redact(prepResp);
        Assert.DoesNotContain("aabbccdd", red2);
        Assert.Contains("123", red2);      // fee survives — still useful for debugging
        Assert.Contains("hash1", red2);    // tx hash is public data
    }

    [Fact]
    public void Redacts_Case_Insensitively_And_When_Nested()
    {
        string json = "{\"outer\":{\"Seed\":\"a b c\",\"note\":\"keep\"},\"list\":[{\"password\":\"pw\"}]}";
        string red = SecretRedactor.Redact(json);
        Assert.DoesNotContain("a b c", red);
        Assert.DoesNotContain("pw", red);
        Assert.Contains("keep", red);
    }
}
