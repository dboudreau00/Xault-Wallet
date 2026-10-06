using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XaultWallet.E2E;

/// <summary>
/// The whole product, end to end, through the UI only: create a vault (verified seed backup, duress
/// password with a decoy), unlock each wallet, receive mined coins, send to the decoy, confirm in
/// History, and see the decoy receive it. Mining on the private regtest chain is the one thing done
/// outside the UI; it stands in for "someone pays this wallet".
/// </summary>
public sealed partial class WalletScenario
{
    // Test-only passwords, strong enough for the app's estimator.
    public const string MainPassword = "Tundra-Violin-Marble-9431";
    public const string DuressPassword = "Orchid-Lantern-Basalt-2786";
    private const decimal SendAmount = 12.5m;

    private readonly IAppDriver _app;
    private readonly TestChain _chain;
    private readonly Action<string> _log;
    private readonly double _timeScale;
    private readonly bool _installWalletRpc;
    private readonly Dictionary<int, string> _seed = new();
    private int _shot;

    /// <param name="installWalletRpc">The app starts without monero-wallet-rpc: the scenario begins
    /// by installing it with the startup screen's "Download &amp; install" (signed list, checksum).</param>
    public WalletScenario(IAppDriver app, TestChain chain, Action<string> log, double timeScale = 1.0, bool installWalletRpc = false)
    {
        _app = app;
        _chain = chain;
        _log = log;
        _timeScale = timeScale;
        _installWalletRpc = installWalletRpc;
    }

    /// <summary>What the install reported, when the scenario installed monero-wallet-rpc.</summary>
    public string InstallResult { get; private set; } = string.Empty;

    public string DecoyAddress { get; private set; } = string.Empty;

    public string MainAddress { get; private set; } = string.Empty;

    public string SentTxId { get; private set; } = string.Empty;

    public async Task RunAsync()
    {
        await StepAsync("Startup splash hands over to vault creation", StartupAsync);
        await StepAsync("Network: the local regtest node is recognised", NetworkAsync);
        await StepAsync("Generate the recovery seed", GenerateSeedAsync);
        await StepAsync("Verify three words of the backup", VerifySeedAsync);
        await StepAsync("Vault password", PasswordAsync);
        await StepAsync("Duress password and decoy seed", DuressAsync);
        await StepAsync("Create the vault", CreateVaultAsync);
        await StepAsync("Unlock with the duress password: decoy wallet", async () => DecoyAddress = await UnlockAsync(DuressPassword, "decoy-wallet"));
        await StepAsync("Lock", LockAsync);
        await StepAsync("Unlock with the main password: main wallet", async () =>
        {
            MainAddress = await UnlockAsync(MainPassword, "main-wallet");
            Check(MainAddress != DecoyAddress, "the two passwords opened the same wallet");
        });
        await StepAsync("Receive: 80 blocks mined to the main wallet", ReceiveAsync);
        await StepAsync($"Send {SendAmount} XMR to the decoy wallet", SendAsync);
        await StepAsync("History shows the confirmed payment", HistoryAsync);
        await StepAsync("Lock", LockAsync);
        await StepAsync("The decoy wallet received the payment", DecoyReceivedAsync);
        await StepAsync("Lock", LockAsync);
        await StepAsync("The main wallet reopens with its funds and history", MainAgainAsync);
        await StepAsync("Settings opens and closes", SettingsAsync);
        await StepAsync("Lock", LockAsync);
    }

    // ------------------------------------------------------------------ steps

    private async Task StartupAsync()
    {
        if (_installWalletRpc)
        {
            // No monero-wallet-rpc anywhere: startup must stop and offer to set it up, and the button
            // must fetch it from getmonero.org, verify binaryFate's signature and the checksum, and
            // install it. Then startup carries on by itself.
            await _app.WaitForAsync("Startup.BackendSetup", Seconds(60), enabled: false);
            await ShotAsync("backend-setup");
            await _app.ClickAsync("WalletRpc.Install");
            InstallResult = await EventuallyAsync(
                async () => await _app.IsVisibleAsync("WalletRpc.Result") ? await _app.ReadTextAsync("WalletRpc.Result") : string.Empty,
                text => text.Length > 0,
                Seconds(900),
                "the install result");
            Check(InstallResult.StartsWith("Installed monero-wallet-rpc", StringComparison.Ordinal), "the install failed: " + InstallResult);
            await ShotAsync("backend-installed");
        }

        // The splash checks the binary and the default node, then routes to vault creation. When the
        // default node isn't up it offers "Continue anyway": take it instead of waiting out retries.
        await EventuallyAsync(async () =>
        {
            if (await _app.IsVisibleAsync("Startup.Continue"))
            {
                await _app.ClickAsync("Startup.Continue");
            }

            return await _app.IsVisibleAsync("Create.Submit");
        }, ok => ok, Seconds(120), "the create-vault screen");
        await ShotAsync("create-vault");
    }

    private async Task NetworkAsync()
    {
        await _app.SelectAsync("Create.Network", "Mainnet"); // regtest uses mainnet-format addresses
        await _app.TypeAsync("Create.Daemon", _chain.DaemonUrl);
        await _app.WaitForAsync("Create.TestChainNotice", Seconds(20), enabled: false);
        Check(!await _app.IsVisibleAsync("Create.MainnetWarning"), "a local test chain is presented as real-funds mainnet");
        await ShotAsync("network");
    }

    private async Task GenerateSeedAsync()
    {
        await _app.ClickAsync("Create.GenerateSeed");
        IReadOnlyList<string> texts = await EventuallyAsync(
            () => _app.ReadTextsAsync("Create.SeedWords"),
            t => ParseSeed(t).Count == 25,
            Seconds(150),
            "25 seed words",
            failWhenVisible: "Create.Error");
        foreach ((int n, string word) in ParseSeed(texts))
        {
            _seed[n] = word;
        }

        await ShotAsync("seed");
    }

    private async Task VerifySeedAsync()
    {
        await _app.ClickAsync("Create.OpenVerify");
        await _app.WaitForAsync("Verify.Input1", Seconds(15));
        for (int i = 1; i <= 3; i++)
        {
            string prompt = await _app.ReadTextAsync($"Verify.Prompt{i}"); // "Word #7"
            Match m = WordNumber().Match(prompt);
            Check(m.Success, $"unexpected verification prompt \"{prompt}\"");
            await _app.TypeAsync($"Verify.Input{i}", _seed[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
        }

        await ShotAsync("verify-backup");
        await _app.ClickAsync("Verify.Submit");
        await _app.WaitForAsync("Create.SeedVerified", Seconds(15), enabled: false);
    }

    private async Task PasswordAsync()
    {
        await _app.TypeAsync("Create.Password", MainPassword);
        await _app.TypeAsync("Create.PasswordConfirm", MainPassword);
        string strength = await EventuallyAsync(() => _app.ReadTextAsync("Create.Strength"), s => s.Length > 0, Seconds(10), "the strength meter");
        Check(!strength.StartsWith("Too easy", StringComparison.Ordinal), $"the test password was rated \"{strength}\"");
        await ShotAsync("password");
    }

    private async Task DuressAsync()
    {
        await _app.SetCheckedAsync("Create.EnableDuress", true);
        await _app.TypeAsync("Create.DuressPassword", DuressPassword);
        await _app.ClickAsync("Create.GenerateDecoy");
        string decoy = await EventuallyAsync(
            () => _app.ReadTextAsync("Create.DecoySeed"),
            s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 25,
            Seconds(150),
            "the decoy seed",
            failWhenVisible: "Create.Error");
        Check(decoy != string.Join(' ', _seed.OrderBy(p => p.Key).Select(p => p.Value)), "the decoy seed equals the main seed");
        await ShotAsync("duress");
    }

    private async Task CreateVaultAsync()
    {
        await _app.ClickAsync("Create.Submit");
        await WaitOrFailAsync("Unlock.Password", "Create.Error", Seconds(120)); // two Argon2id derivations
        await ShotAsync("unlock");
    }

    private async Task<string> UnlockAsync(string password, string shot)
    {
        await _app.TypeAsync("Unlock.Password", password);
        await _app.ClickAsync("Unlock.Submit");
        string address = await EventuallyAsync(
            async () => await _app.IsVisibleAsync("Receive.Address") ? await _app.ReadTextAsync("Receive.Address") : string.Empty,
            a => a.Length >= 95,
            Seconds(240),
            "the wallet's address",
            failWhenVisible: "Unlock.Error",
            alsoFailWhenVisible: "Wallet.StartupFailed");

        string badge = await _app.ReadTextAsync("Wallet.NetworkBadge");
        Check(badge.Contains("Regtest", StringComparison.Ordinal), $"network badge reads \"{badge}\"");
        Check(!await _app.IsVisibleAsync("Wallet.MainnetBadge"), "a local test chain is labelled as mainnet");
        await EventuallyAsync(() => _app.ReadTextAsync("Wallet.SyncText"), s => s.StartsWith("Synced", StringComparison.Ordinal),
            Seconds(240), "sync to the chain tip", nudge: RefreshAsync);
        await ShotAsync(shot);
        return address;
    }

    private async Task LockAsync()
    {
        await _app.ClickAsync("Wallet.Lock");
        await _app.WaitForAsync("Unlock.Password", Seconds(60));
    }

    private async Task ReceiveAsync()
    {
        await _chain.MineAsync(MainAddress, 80);
        decimal spendable = await EventuallyAsync(
            async () => Xmr(await _app.ReadTextAsync("Wallet.Spendable")),
            v => v > SendAmount + 1m,
            Seconds(300),
            "a spendable balance",
            nudge: RefreshAsync);
        decimal total = Xmr(await _app.ReadTextAsync("Wallet.Balance"));
        Check(total >= spendable, $"balance {total} is below the spendable {spendable}");
        _log($"  balance {total} XMR, spendable {spendable} XMR");
        await ShotAsync("funded");

        // Copy the address: a toast confirms it on whichever tab is open.
        await _app.ClickAsync("Receive.Copy");
        string toast = await EventuallyAsync(() => _app.ReadTextAsync("Wallet.Toast"), t => t.Length > 0, Seconds(10), "the copy toast");
        Check(toast.StartsWith("Address copied", StringComparison.Ordinal), $"copy feedback reads \"{toast}\"");
        await ShotAsync("address-copied");
    }

    private async Task SendAsync()
    {
        await _app.ClickAsync("Wallet.Tab.Send");
        await _app.TypeAsync("Send.Address", DecoyAddress);
        await _app.TypeAsync("Send.Amount", SendAmount.ToString(CultureInfo.InvariantCulture));
        await EventuallyAsync(() => _app.ReadTextAsync("Send.AmountPreview"), s => s.StartsWith("= 12.5 XMR", StringComparison.Ordinal),
            Seconds(10), "the amount read-back");
        await ShotAsync("send-form");

        await _app.ClickAsync("Send.Review");
        await WaitOrFailAsync("SendConfirm.Send", "Send.Result", Seconds(180)); // builds and signs, no broadcast
        Check(await _app.ReadTextAsync("SendConfirm.Recipient") == DecoyAddress, "the confirmation shows a different recipient");
        Check(await _app.ReadTextAsync("SendConfirm.Amount") == "12.5 XMR", "the confirmation shows a different amount");
        decimal fee = Xmr(await _app.ReadTextAsync("SendConfirm.Fee"));
        Check(fee > 0m && fee < 0.1m, $"implausible fee {fee}");
        decimal total = Xmr(await _app.ReadTextAsync("SendConfirm.Total"));
        Check(total == SendAmount + fee, $"total {total} is not amount + fee");
        await ShotAsync("send-confirm");

        await _app.ClickAsync("SendConfirm.Send");
        string result = await EventuallyAsync(() => _app.ReadTextAsync("Send.Result"), s => s.Length > 0, Seconds(120), "the send result");
        Check(result == "Sent 12.5 XMR", $"send result: {result}");
        string detail = await _app.ReadTextAsync("Send.ResultDetail");
        Check(detail.StartsWith("Network fee ", StringComparison.Ordinal) && Xmr(detail) == fee, $"send detail: {detail}");
        SentTxId = await _app.ReadTextAsync("Send.LastTxId");
        Check(TxId().IsMatch(SentTxId), $"payment proof shows txid \"{SentTxId}\"");
        Check(TxId().IsMatch(await _app.ReadTextAsync("Send.LastTxKey")), "payment proof has no tx key");
        await ShotAsync("sent");
    }

    private async Task HistoryAsync()
    {
        await _chain.MineAsync(MainAddress, 12); // confirm it (received outputs unlock after 10)
        await _app.ClickAsync("Wallet.Tab.History");
        await EventuallyAsync(
            () => _app.ReadTextsAsync("History.List"),
            t => t.Contains("Sent") && t.Any(x => x.Contains("12.5 XMR", StringComparison.Ordinal)) && !t.Contains("pending"),
            Seconds(240),
            "the confirmed payment in History",
            nudge: RefreshAsync);
        await ShotAsync("history");
    }

    private async Task DecoyReceivedAsync()
    {
        string again = await UnlockAsync(DuressPassword, "decoy-wallet-again");
        Check(again == DecoyAddress, "the duress password opened a different wallet this time");
        await EventuallyAsync(async () => Xmr(await _app.ReadTextAsync("Wallet.Balance")), b => b == SendAmount,
            Seconds(240), "the decoy's balance", nudge: RefreshAsync);
        await EventuallyAsync(async () => Xmr(await _app.ReadTextAsync("Wallet.Spendable")), b => b == SendAmount,
            Seconds(240), "the decoy's spendable balance", nudge: RefreshAsync);
        await _app.ClickAsync("Wallet.Tab.History");
        await EventuallyAsync(() => _app.ReadTextsAsync("History.List"),
            t => t.Contains("Received") && t.Any(x => x.Contains("+12.5 XMR", StringComparison.Ordinal)),
            Seconds(120), "the payment in the decoy's history", nudge: RefreshAsync);
        await ShotAsync("decoy-received");
    }

    private async Task MainAgainAsync()
    {
        string again = await UnlockAsync(MainPassword, "main-wallet-again");
        Check(again == MainAddress, "the main password opened a different wallet this time");
        await _app.ClickAsync("Wallet.Tab.History");
        await EventuallyAsync(() => _app.ReadTextsAsync("History.List"),
            t => t.Contains("Sent") && t.Any(x => x.Contains("12.5 XMR", StringComparison.Ordinal)),
            Seconds(180), "the payment in the main wallet's history after reopening", nudge: RefreshAsync);
        await ShotAsync("main-history-again");
    }

    private async Task SettingsAsync()
    {
        await _app.ClickAsync("Shell.Settings");
        await _app.WaitForAsync("Settings.Close", Seconds(20));
        await ShotAsync("settings");
        await _app.ClickAsync("Settings.Close");
        await _app.WaitForAsync("Wallet.Lock", Seconds(20));
    }

    // ------------------------------------------------------------------ helpers

    private async Task RefreshAsync()
    {
        try
        {
            if (await _app.IsVisibleAsync("Wallet.Refresh"))
            {
                await _app.ClickAsync("Wallet.Refresh");
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // a refresh already in flight keeps the button disabled; the next poll tries again
        }
    }

    private async Task StepAsync(string name, Func<Task> body)
    {
        _log("> " + name);
        var sw = Stopwatch.StartNew();
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            _log($"FAILED: {name} after {sw.Elapsed.TotalSeconds:0.0}s: {ex.Message}");
            try { await _app.ScreenshotAsync($"{++_shot:00}-FAILED"); } catch { /* best effort */ }
            try { _log(await _app.DescribeScreenAsync()); } catch { /* best effort */ }
            throw;
        }

        _log($"  ok ({sw.Elapsed.TotalSeconds:0.0}s)");
    }

    private Task ShotAsync(string name) => _app.ScreenshotAsync($"{++_shot:00}-{name}");

    private TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s * _timeScale);

    private async Task WaitOrFailAsync(string id, string errorId, TimeSpan timeout) =>
        await EventuallyAsync(() => _app.IsVisibleAsync(id), ok => ok, timeout, $"'{id}'", failWhenVisible: errorId);

    /// <summary>Poll until <paramref name="ok"/> holds. Fails at once (with the app's own message) when
    /// an error element appears; <paramref name="nudge"/> runs every few seconds (e.g. Refresh).</summary>
    private async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> ok, TimeSpan timeout, string what,
        string? failWhenVisible = null, string? alsoFailWhenVisible = null, Func<Task>? nudge = null)
    {
        var sw = Stopwatch.StartNew();
        TimeSpan nextNudge = TimeSpan.FromSeconds(6);
        string last = "(nothing yet)";
        while (true)
        {
            foreach (string? errorId in new[] { failWhenVisible, alsoFailWhenVisible })
            {
                if (errorId is not null && await _app.IsVisibleAsync(errorId))
                {
                    string message = await _app.ReadTextAsync(errorId);
                    if (message.Length > 0)
                    {
                        throw new InvalidOperationException($"waiting for {what}, the app reported: {message}");
                    }
                }
            }

            try
            {
                T value = await read();
                if (ok(value))
                {
                    return value;
                }

                last = value is IEnumerable<string> list ? string.Join(" | ", list) : value?.ToString() ?? "null";
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or FormatException)
            {
                last = ex.Message;
            }

            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException($"{what}: not there after {timeout.TotalSeconds:0}s. Last seen: {Trim(last)}");
            }

            if (nudge is not null && sw.Elapsed > nextNudge)
            {
                await nudge();
                nextNudge = sw.Elapsed + TimeSpan.FromSeconds(6);
            }

            await Task.Delay(400);
        }
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failure);
        }
    }

    /// <summary>"Spendable 351.75 XMR" / "1406.2" → the number. Masked amounts don't parse.</summary>
    private static decimal Xmr(string text)
    {
        Match m = Number().Match(text);
        if (!m.Success)
        {
            throw new FormatException($"no amount in \"{text}\"");
        }

        return decimal.Parse(m.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    /// <summary>The seed grid reads "1", "word", "2", "word", ...</summary>
    private static List<(int, string)> ParseSeed(IReadOnlyList<string> texts)
    {
        var words = new List<(int, string)>();
        for (int i = 0; i + 1 < texts.Count; i++)
        {
            if (int.TryParse(texts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && texts[i + 1].All(char.IsLetter))
            {
                words.Add((n, texts[i + 1]));
                i++;
            }
        }

        return words;
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex WordNumber();

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex TxId();
}
