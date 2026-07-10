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
/// Recommended: point them at STAGENET, never mainnet with real funds.
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

    private static WalletSecrets Secrets(string mnemonic, ulong height, ProfileKind kind, bool wipeReal = false) => new()
    {
        Kind = kind,
        Label = kind == ProfileKind.Real ? "Main" : "Wallet",
        Network = IntegrationEnv.Network,
        Mnemonic = mnemonic,
        RestoreHeight = height,
        DaemonAddress = IntegrationEnv.Daemon!,
        EphemeralWalletPassword = Convert.ToHexString(VaultCrypto.RandomBytes(24)),
        DuressWipeReal = wipeReal,
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

        ulong height = await MoneroDiagnostics.ProbeDaemonAsync(IntegrationEnv.Daemon!);
        _out.WriteLine("daemon height: " + height);
        Assert.True(height > 0);
    }

    [Fact]
    public async Task Generate_Then_Reopen_Seed_RoundTrip()
    {
        if (Skip()) { return; }

        var svc = new MoneroWalletService(IntegrationEnv.WalletRpc!);
        (string mnemonic, ulong height) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);

        Assert.False(string.IsNullOrWhiteSpace(mnemonic));
        Assert.Equal(25, mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        _out.WriteLine($"generated seed at height {height}");

        // The generated seed must reopen into a valid wallet with an address.
        string address = await svc.ValidateSeedOpensAsync(Secrets(mnemonic, height, ProfileKind.Real));
        Assert.False(string.IsNullOrWhiteSpace(address));
        _out.WriteLine("primary address: " + address);
    }

    [Fact]
    public async Task Vault_Create_Unlock_With_Generated_Seed()
    {
        if (Skip()) { return; }

        var svc = new MoneroWalletService(IntegrationEnv.WalletRpc!);
        (string mnemonic, ulong height) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);

        string vaultPath = Path.Combine(Path.GetTempPath(), "xw_it_" + Guid.NewGuid().ToString("N") + ".xv");
        try
        {
            using (var pw = Pw("integration-main-password"))
            {
                VaultManager.Create(vaultPath, pw, Secrets(mnemonic, height, ProfileKind.Real), null, null);
            }

            var mgr = VaultManager.Load(vaultPath);
            using (var pw = Pw("integration-main-password"))
            {
                UnlockResult? result = mgr.Unlock(pw);
                Assert.NotNull(result);
                Assert.False(result!.WasDuress);
                Assert.Equal(mnemonic, result.Secrets.Mnemonic);
            }
        }
        finally
        {
            if (File.Exists(vaultPath)) { File.Delete(vaultPath); }
        }
    }

    [Fact]
    public async Task Duress_Password_Opens_Decoy_Not_Real()
    {
        if (Skip()) { return; }

        var svc = new MoneroWalletService(IntegrationEnv.WalletRpc!);
        (string realSeed, ulong realH) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        (string decoySeed, ulong decoyH) = await svc.GenerateNewSeedAsync(IntegrationEnv.Network, IntegrationEnv.Daemon!);
        Assert.NotEqual(realSeed, decoySeed);

        string vaultPath = Path.Combine(Path.GetTempPath(), "xw_it_" + Guid.NewGuid().ToString("N") + ".xv");
        try
        {
            using (var main = Pw("real-password-123"))
            using (var duress = Pw("decoy-password-456"))
            {
                VaultManager.Create(
                    vaultPath,
                    main, Secrets(realSeed, realH, ProfileKind.Real),
                    duress, Secrets(decoySeed, decoyH, ProfileKind.Duress));
            }

            var mgr = VaultManager.Load(vaultPath);

            using (var main = Pw("real-password-123"))
            {
                UnlockResult? r = mgr.Unlock(main);
                Assert.NotNull(r);
                Assert.False(r!.WasDuress);
                Assert.Equal(realSeed, r.Secrets.Mnemonic);
            }

            using (var duress = Pw("decoy-password-456"))
            {
                UnlockResult? r = mgr.Unlock(duress);
                Assert.NotNull(r);
                Assert.True(r!.WasDuress);
                Assert.Equal(decoySeed, r.Secrets.Mnemonic);
            }
        }
        finally
        {
            if (File.Exists(vaultPath)) { File.Delete(vaultPath); }
        }
    }
}
