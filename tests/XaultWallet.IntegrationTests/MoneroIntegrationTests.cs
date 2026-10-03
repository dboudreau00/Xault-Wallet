using System.Net;
using System.Text;
using XaultWallet.Core.Models;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;
using Xunit;
using Xunit.Abstractions;

namespace XaultWallet.IntegrationTests;

/// <summary>
/// End-to-end tests that exercise the REAL monero-wallet-rpc binary and a daemon. They only
/// do work when XW_WALLET_RPC and XW_DAEMON are set (see <see cref="IntegrationEnv"/>);
/// otherwise each test logs a skip notice and returns so CI stays green without a node.
///
/// Recommended: a private regtest chain (CI does this), or stagenet. Never mainnet with real funds.
/// </summary>
public sealed class MoneroIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public MoneroIntegrationTests(ITestOutputHelper output) => _out = output;

    private bool Skip()
    {
        if (IntegrationEnv.Configured)
        {
            return false;
        }

        _out.WriteLine(IntegrationEnv.SkipReason);
        return true;
    }

    private static SecureBuffer Pw(string s) => SecureBuffer.FromPassword(s.ToCharArray());

    internal static WalletSecrets Secrets(string mnemonic, ulong height, bool wipeOther = false) => new()
    {
        Network = IntegrationEnv.Network,
        Mnemonic = mnemonic,
        RestoreHeight = height,
        DaemonAddress = IntegrationEnv.Daemon!,
        EphemeralWalletPassword = Convert.ToHexString(VaultCrypto.RandomBytes(24)),
        WipeOtherSlotOnUnlock = wipeOther,
    };

    [Fact]
    public async Task WalletRpc_Binary_Reports_Version()
    {
        if (Skip()) { return; }

        string version = await MoneroDiagnostics.ProbeWalletRpcAsync(IntegrationEnv.WalletRpc!);
        _out.WriteLine("wallet-rpc version: " + version);
        Assert.Contains("Monero", version, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Daemon_Is_Reachable()
    {
        if (Skip()) { return; }

        ulong height = await MoneroDiagnostics.ProbeDaemonAsync(IntegrationEnv.Daemon!, proxyAddress: null);
        _out.WriteLine("daemon height: " + height);
        Assert.True(height > 0);
    }

    [Fact]
    public async Task Backend_Rejects_Unauthenticated_And_Cross_Origin_Requests()
    {
        if (Skip()) { return; }

        await using var pm = new MoneroProcessManager(IntegrationEnv.WalletRpc!, IntegrationEnv.Options);
        using MoneroRpcClient client = await pm.StartServerAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);

        // The session client itself works...
        Assert.True((await client.GetVersionAsync()).Version > 0);

        // ...but what a web page can send (a CORS "simple" text/plain POST, any Host) is refused.
        // Before the fix this exact request opened a wallet and query_key returned the seed.
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        foreach (string method in new[] { "get_version", "query_key", "create_wallet" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(pm.Endpoint!, "/json_rpc"))
            {
                Content = new StringContent(
                    $"{{\"jsonrpc\":\"2.0\",\"id\":\"0\",\"method\":\"{method}\",\"params\":{{\"key_type\":\"mnemonic\",\"filename\":\"x\",\"password\":\"\",\"language\":\"English\"}}}}",
                    Encoding.UTF8, "text/plain"),
            };
            req.Headers.Host = "evil.example";
            using HttpResponseMessage resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Session_Artifacts_Stay_Inside_The_Shredded_Directory()
    {
        if (Skip()) { return; }

        string cwdLog = Path.Combine(Directory.GetCurrentDirectory(), "monero-wallet-rpc.log");
        bool cwdLogExisted = File.Exists(cwdLog);

        var pm = new MoneroProcessManager(IntegrationEnv.WalletRpc!, IntegrationEnv.Options);
        string session;
        try
        {
            using MoneroRpcClient client = await pm.StartServerAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
            session = pm.SessionDirectory!;

            // Credentials file is shredded as soon as the server is up; the password never hits argv.
            Assert.False(File.Exists(Path.Combine(session, "rpc.conf")));
            if (OperatingSystem.IsLinux())
            {
                string cmdline = File.ReadAllText($"/proc/{pm.ProcessId}/cmdline").Replace('\0', ' ');
                Assert.Contains("--config-file", cmdline);
                Assert.DoesNotContain("rpc-login", cmdline);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                             File.GetUnixFileMode(session));
            }

            // Opening a wallet creates the ring database — inside the session, not in ~/.shared-ringdb.
            await client.CreateWalletAsync("probe", "pw");
            Assert.True(Directory.EnumerateFiles(Path.Combine(session, "ringdb"), "*", SearchOption.AllDirectories).Any());
            Assert.True(File.Exists(Path.Combine(session, "wallet-rpc.log")));
            Assert.True(File.Exists(Path.Combine(session, "wallet", "probe.keys")));
        }
        finally
        {
            await pm.DisposeAsync();
        }

        Assert.False(Directory.Exists(session));
        Assert.Equal(cwdLogExisted, File.Exists(cwdLog)); // nothing new in the CWD
    }

    [Fact]
    public async Task Generated_Seed_Restore_Height_Tracks_The_Daemon_Tip()
    {
        if (Skip()) { return; }

        await using MoneroWalletService svc = IntegrationEnv.NewService();
        ulong margin = MoneroWalletService.GeneratedSeedRestoreMargin;
        ulong tip0 = await MoneroDiagnostics.ProbeDaemonAsync(IntegrationEnv.Daemon!, null);
        if (IntegrationEnv.IsRegtest && tip0 <= margin + 10)
        {
            // On a young chain the margin floors the height at 0, which would make this test
            // vacuous. Grow the chain past the margin first.
            (string s, ulong h) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
            await IntegrationEnv.MineAsync(await svc.ValidateSeedOpensAsync(Secrets(s, h)), (int)(margin + 50 - tip0));
        }

        ulong tipBefore = await MoneroDiagnostics.ProbeDaemonAsync(IntegrationEnv.Daemon!, null);
        (_, ulong height) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        ulong tipAfter = await MoneroDiagnostics.ProbeDaemonAsync(IntegrationEnv.Daemon!, null);
        _out.WriteLine($"tip {tipBefore}..{tipAfter}, sealed restore height {height}");

        // Previously this came from wallet-rpc's get_height right after create_wallet, which is 1 —
        // a "new" wallet then rescanned the entire chain on every unlock.
        ulong expectedFloor = tipBefore > margin ? tipBefore - margin : 0;
        Assert.InRange(height, expectedFloor, tipAfter);
    }

    [Fact]
    public async Task Generate_Then_Reopen_Seed_RoundTrip()
    {
        if (Skip()) { return; }

        await using MoneroWalletService svc = IntegrationEnv.NewService();
        (string mnemonic, ulong height) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);

        Assert.False(string.IsNullOrWhiteSpace(mnemonic));
        Assert.Equal(25, mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        string address = await svc.ValidateSeedOpensAsync(Secrets(mnemonic, height));
        Assert.Null(MoneroAddress.Problem(address, IntegrationEnv.Network));
    }

    [Fact]
    public async Task Duress_Password_Opens_Decoy_Not_Real()
    {
        if (Skip()) { return; }

        await using MoneroWalletService svc = IntegrationEnv.NewService();
        (string realSeed, ulong realH) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        (string decoySeed, ulong decoyH) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        Assert.NotEqual(realSeed, decoySeed);

        string vaultPath = Path.Combine(Path.GetTempPath(), "xw_it_" + Guid.NewGuid().ToString("N") + ".xv");
        try
        {
            using (var main = Pw("real-password-123"))
            using (var duress = Pw("decoy-password-456"))
            {
                VaultManager.Create(vaultPath, main, Secrets(realSeed, realH), duress, Secrets(decoySeed, decoyH, wipeOther: true));
            }

            using (var duress = Pw("decoy-password-456"))
            {
                WalletSecrets opened = VaultManager.Load(vaultPath).Unlock(duress)!.Secrets;
                Assert.Equal(decoySeed, opened.Mnemonic);

                // The decoy really opens as a working wallet through the hardened backend.
                string address = await svc.ValidateSeedOpensAsync(opened);
                Assert.Null(MoneroAddress.Problem(address, IntegrationEnv.Network));
            }

            using (var main = Pw("real-password-123"))
            {
                Assert.Null(VaultManager.Load(vaultPath).Unlock(main)); // wiped by the duress unlock
            }
        }
        finally
        {
            if (File.Exists(vaultPath)) { File.Delete(vaultPath); }
        }
    }
}

/// <summary>
/// Real money movement on a private regtest chain: mining, the prepare → confirm → relay send with
/// the exact fee, payment proofs, history and sweep-all. Runs only when XW_NETWORK=regtest.
/// </summary>
public sealed class RegtestFundsFlowTests
{
    private readonly ITestOutputHelper _out;

    public RegtestFundsFlowTests(ITestOutputHelper output) => _out = output;

    private bool Skip()
    {
        if (IntegrationEnv.Configured && IntegrationEnv.IsRegtest)
        {
            return false;
        }

        _out.WriteLine("SKIPPED: funds-flow tests need XW_NETWORK=regtest (a private monerod --regtest chain).");
        return true;
    }

    private static async Task<(WalletSecrets secrets, string address)> NewWalletAsync()
    {
        await using MoneroWalletService gen = IntegrationEnv.NewService();
        (string seed, ulong height) = await gen.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        WalletSecrets secrets = MoneroIntegrationTests.Secrets(seed, height);
        return (secrets, await gen.ValidateSeedOpensAsync(secrets));
    }

    [Fact]
    public async Task Prepared_Send_Pays_The_Shown_Fee_Proves_Payment_And_Sweeps_Back()
    {
        if (Skip()) { return; }

        (WalletSecrets alice, string aliceAddr) = await NewWalletAsync();
        (WalletSecrets bob, string bobAddr) = await NewWalletAsync();

        // Fund Alice: coinbase outputs unlock after 60 blocks.
        await IntegrationEnv.MineAsync(aliceAddr, 80);

        string txHash;
        ulong fee;
        string txKey;
        const decimal amount = 1.25m;
        await using (MoneroWalletService a = IntegrationEnv.NewService())
        {
            await a.OpenAsync(alice);
            await a.RefreshAsync();
            (decimal balance, decimal unlocked) = await a.GetBalanceAsync();
            _out.WriteLine($"alice balance {balance} (unlocked {unlocked})");
            Assert.True(unlocked > amount);

            // Prepare: built and signed but NOT broadcast — the fee shown is the fee paid.
            TransferResult prepared = await a.PrepareSendAsync(bobAddr, amount, priority: 1);
            Assert.True(prepared.Fee > 0);
            Assert.Equal(MoneroRpcClient.XmrToAtomic(amount), prepared.Amount);
            Assert.False(string.IsNullOrWhiteSpace(prepared.TxMetadata));
            Assert.False(string.IsNullOrWhiteSpace(prepared.TxKey));

            txHash = await a.RelaySendAsync(prepared.TxMetadata);
            Assert.Equal(prepared.TxHash, txHash);
            fee = prepared.Fee;
            txKey = prepared.TxKey;

            await IntegrationEnv.MineAsync(aliceAddr, 12); // confirm (outputs unlock after 10)
            await a.RefreshAsync();

            // Payment proof: tx key + txid + destination verifies exactly the sent amount.
            (ulong received, ulong confirmations, bool inPool) = await a.CheckTxKeyAsync(txHash, txKey, bobAddr);
            Assert.Equal(MoneroRpcClient.XmrToAtomic(amount), received);
            Assert.False(inPool);
            Assert.True(confirmations >= 10);
            Assert.Equal(txKey, await a.GetTxKeyAsync(txHash));

            IReadOnlyList<TransferEntry> history = await a.GetHistoryAsync();
            TransferEntry outgoing = Assert.Single(history, t => t.TxId == txHash);
            Assert.Equal("out", outgoing.Type);
            Assert.Equal(fee, outgoing.Fee);
        }

        await using (MoneroWalletService b = IntegrationEnv.NewService())
        {
            await b.OpenAsync(bob);
            await b.RefreshAsync();
            (decimal bobBalance, decimal bobUnlocked) = await b.GetBalanceAsync();
            Assert.Equal(amount, bobBalance);
            Assert.Equal(amount, bobUnlocked);

            // Send max: sweep everything back, then nothing is left.
            SweepAllResult sweep = await b.PrepareSweepAllAsync(aliceAddr, priority: 0);
            ulong swept = 0, sweepFees = 0;
            for (int i = 0; i < sweep.TxMetadataList.Count; i++)
            {
                await b.RelaySendAsync(sweep.TxMetadataList[i]);
                swept += sweep.AmountList[i];
                sweepFees += sweep.FeeList[i];
            }

            Assert.Equal(MoneroRpcClient.XmrToAtomic(amount), swept + sweepFees);
            await IntegrationEnv.MineAsync(aliceAddr, 2);
            await b.RefreshAsync();
            Assert.Equal(0m, (await b.GetBalanceAsync()).balance);
        }
    }
}
