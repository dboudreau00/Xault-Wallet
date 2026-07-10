using XaultWallet.Core.Monero;

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
        Directory.CreateDirectory(DataDirectory);
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

    /// <summary>What monero-wallet-rpc resolves to when the user hasn't set an explicit path.</summary>
    public string ResolvedDefaultWalletRpcBinary => ResolveDefaultWalletRpcBinary();

    /// <summary>The path actually used to launch monero-wallet-rpc (explicit override, else default).</summary>
    public string WalletRpcBinaryPath =>
        string.IsNullOrWhiteSpace(Settings.WalletRpcBinaryPath)
            ? ResolveDefaultWalletRpcBinary()
            : Settings.WalletRpcBinaryPath.Trim();

    public string DefaultDaemonAddress => Settings.DefaultDaemonAddress;

    public int DefaultNetworkIndex => Settings.DefaultNetworkIndex;

    public int AutoRefreshSeconds => Settings.AutoRefreshSeconds;

    public void SaveSettings() => Settings.Save(SettingsPath);

    public MoneroWalletService CreateWalletService() => new(WalletRpcBinaryPath);

    private static string ResolveDefaultWalletRpcBinary()
    {
        string exe = OperatingSystem.IsWindows() ? "monero-wallet-rpc.exe" : "monero-wallet-rpc";

        // Look next to our own binary first (bundled), then rely on PATH.
        string local = Path.Combine(AppContext.BaseDirectory, exe);
        return File.Exists(local) ? local : exe;
    }
}
