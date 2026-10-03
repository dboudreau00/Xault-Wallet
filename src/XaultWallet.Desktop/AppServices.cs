using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop;

/// <summary>
/// Simple app-wide service/state holder. In a larger build this would be a DI
/// container; for clarity it is a single object passed between view models.
/// </summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    private AppServices()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XaultWallet");
        // 0700 folder, 0600 files — and tighten what an older version created with the umask.
        PrivateFiles.EnsureDirectory(DataDirectory);
        PrivateFiles.TightenTree(DataDirectory);
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        VaultPath = Path.Combine(DataDirectory, "vault.xv");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        Settings = AppSettings.Load(SettingsPath);
    }

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string VaultPath { get; set; }

    public string SettingsPath { get; }

    public AppSettings Settings { get; }

    /// <summary>What monero-wallet-rpc resolves to when the user hasn't set an explicit path
    /// (empty when it is neither next to the app nor on PATH).</summary>
    public string ResolvedDefaultWalletRpcBinary => ResolveDefaultWalletRpcBinary();

    /// <summary>The path actually used to launch monero-wallet-rpc (explicit override, else default).
    /// A bare name typed in Settings is looked up on PATH; a relative path is passed through for the
    /// launcher to refuse (see ExecutableLocator.EnsureLaunchable).</summary>
    public string WalletRpcBinaryPath =>
        string.IsNullOrWhiteSpace(Settings.WalletRpcBinaryPath)
            ? ResolveDefaultWalletRpcBinary()
            : ExecutableLocator.ResolveConfigured(Settings.WalletRpcBinaryPath, Environment.GetEnvironmentVariable("PATH"));

    public string DefaultDaemonAddress => Settings.DefaultDaemonAddress;

    public int DefaultNetworkIndex => Settings.DefaultNetworkIndex;

    public int AutoRefreshSeconds => Settings.AutoRefreshSeconds;
    public int AutoLockMinutes => Settings.AutoLockMinutes;

    public void SaveSettings() => Settings.Save(SettingsPath);

    public MoneroWalletService CreateWalletService() => new(WalletRpcBinaryPath, Settings.ProxyAddress);

    private static string ResolveDefaultWalletRpcBinary()
    {
        string exe = ExecutableLocator.WalletRpcFileName;

        // Next to our own binary first, then PATH — always resolved to an absolute path, because the
        // launcher (rightly) refuses bare names. Previously a bare name was returned here, so
        // "leave blank to auto-detect" could never actually launch a PATH-installed binary.
        string local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local))
        {
            return local;
        }

        return ExecutableLocator.FindOnPath(exe, Environment.GetEnvironmentVariable("PATH")) ?? string.Empty;
    }
}
