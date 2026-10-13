using System.Text.Json;

namespace XaultWallet.E2E;

/// <summary>
/// The browser companion, end to end: the requests a browser extension sends go through the real native
/// messaging host (XaultWallet started the way Chrome starts it) to the running app, and what they do is
/// checked in the app's own UI. Run when the program under test provides <see cref="_companion"/>.
/// </summary>
public sealed partial class WalletScenario
{
    /// <summary>Sends one request as the browser would (JSON in, JSON out), through the native host.</summary>
    private readonly Func<string, Task<string>>? _companion;

    /// <summary>The subaddress the companion asked for.</summary>
    public string CompanionAddress { get; private set; } = string.Empty;

    private const string CompanionLabel = "e2e shop";

    private async Task CompanionStepsAsync()
    {
        if (_companion is null)
        {
            return;
        }

        await StepAsync("Browser companion: status, as the extension sees it", CompanionStatusAsync);
        await StepAsync("Browser companion: a new receive address, labelled, in the Receive list", CompanionReceiveAsync);
        await StepAsync("Browser companion: a payment request opens in the Send tab, with its source", CompanionPayAsync);
        await StepAsync("Browser companion: a request while locked waits for the unlock", CompanionPayWhileLockedAsync);
        await StepAsync("Browser companion: what isn't in the protocol is refused", CompanionRefusesAsync);
    }

    private async Task<JsonElement> AskAsync(string json)
    {
        string reply = await _companion!(json);
        using JsonDocument doc = JsonDocument.Parse(reply);
        return doc.RootElement.Clone();
    }

    private static bool Ok(JsonElement reply) => reply.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement reply, string name) =>
        reply.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

    private static string ErrorCode(JsonElement reply) =>
        reply.TryGetProperty("error", out JsonElement e) && e.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;

    private async Task CompanionStatusAsync()
    {
        JsonElement r = await AskAsync("""{"v":1,"id":"status1","type":"status"}""");
        Check(Ok(r), "status failed: " + r);
        Check(Str(r, "app") == "XaultWallet" && Str(r, "version").Length > 0, "status names the app and its version: " + r);
        Check(Str(r, "state") == "unlocked" && Str(r, "network") == "Regtest", "status says unlocked on the test chain: " + r);
        Check(!r.ToString().Contains(MainAddress, StringComparison.Ordinal) && !r.TryGetProperty("balance", out _) && !r.TryGetProperty("wallet", out _),
            "status gives away more than state and network: " + r);
    }

    private async Task CompanionReceiveAsync()
    {
        JsonElement r = await AskAsync($$"""{"v":1,"id":"receive1","type":"receive","label":"{{CompanionLabel}}","origin":"https://shop.example/checkout"}""");
        Check(Ok(r), "receive failed: " + r);
        CompanionAddress = Str(r, "address");
        Check(CompanionAddress.Length == 95 && CompanionAddress != MainAddress && CompanionAddress[0] == '8',
            "the companion got a subaddress of the wallet: " + CompanionAddress);
        Check(Str(r, "network") == "Regtest", "the reply says which network the address is for: " + r);

        await _app.ClickAsync("Wallet.Tab.Receive");
        string shortForm = CompanionAddress[..10] + "…" + CompanionAddress[^10..];
        await EventuallyAsync(
            () => _app.ReadTextsAsync("Receive.Addresses"),
            texts => texts.Contains(CompanionLabel) && texts.Contains(shortForm),
            Seconds(30),
            "the new address, labelled, in the Receive list");
        await ShotAsync("companion-receive");
    }

    private async Task CompanionPayAsync()
    {
        string uri = $"monero:{CompanionAddress}?tx_amount=0.75&tx_description=E2E%20order";
        JsonElement r = await AskAsync($$"""{"v":1,"id":"pay1","type":"pay","uri":"{{uri}}","origin":"https://shop.example/checkout"}""");
        Check(Ok(r) && Str(r, "result") == "shown", "the payment request wasn't shown: " + r);
        await CheckRequestInSendAsync();
        await ShotAsync("companion-pay");
    }

    private async Task CheckRequestInSendAsync()
    {
        await EventuallyAsync(() => _app.ReadTextAsync("Send.Address"), a => a == CompanionAddress, Seconds(20), "the request's address in the Send tab");
        string amount = await _app.ReadTextAsync("Send.Amount");
        Check(amount == "0.75", "the request's amount: " + amount);
        string note = await _app.ReadTextAsync("Send.PaymentRequest");
        Check(note.Contains("E2E order", StringComparison.Ordinal) && note.Contains("your browser (shop.example)", StringComparison.Ordinal),
            "the request says what it is and where it came from: " + note);
    }

    private async Task CompanionPayWhileLockedAsync()
    {
        await LockAsync();
        string uri = $"monero:{CompanionAddress}?tx_amount=0.75&tx_description=E2E%20order";
        JsonElement r = await AskAsync($$"""{"v":1,"id":"pay2","type":"pay","uri":"{{uri}}","origin":"https://shop.example"}""");
        Check(Ok(r) && Str(r, "result") == "after-unlock", "a request while locked: " + r);

        JsonElement locked = await AskAsync("""{"v":1,"id":"receive2","type":"receive"}""");
        Check(ErrorCode(locked) == "locked", "no address while locked: " + locked);

        await _app.WaitForAsync("Unlock.PendingRequest", Seconds(10), enabled: false);
        string note = await _app.ReadTextAsync("Unlock.PendingRequest");
        Check(note.Contains("your browser (shop.example)", StringComparison.Ordinal), "the unlock screen says a request waits: " + note);
        await ShotAsync("companion-waiting");

        await _app.TypeAsync("Unlock.Password", MainPassword);
        await _app.ClickAsync("Unlock.Submit");
        await CheckRequestInSendAsync();
        await EventuallyAsync(() => _app.ReadTextAsync("Wallet.SyncText"), s => s.StartsWith("Synced", StringComparison.Ordinal),
            Seconds(240), "sync after the unlock", nudge: RefreshAsync);
    }

    private async Task CompanionRefusesAsync()
    {
        JsonElement sign = await AskAsync("""{"v":1,"id":"bad1","type":"sign","tx":"00"}""");
        Check(ErrorCode(sign) == "unknown-type", "an unknown request kind: " + sign);

        JsonElement version = await AskAsync("""{"v":2,"id":"bad2","type":"status"}""");
        Check(ErrorCode(version) == "unsupported-version", "another protocol version: " + version);

        JsonElement notALink = await AskAsync("""{"v":1,"id":"bad3","type":"pay","uri":"https://evil.example/pay"}""");
        Check(ErrorCode(notALink) == "bad-request", "a payment that isn't a monero: link: " + notALink);
    }
}
