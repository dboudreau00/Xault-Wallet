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
        DataDirectory = Path.Combine(UserConfigRoot(), "XaultWallet");
        // 0700 folder, 0600 files — and tighten what an older version created with the umask.
        PrivateFiles.EnsureDirectory(DataDirectory);
        PrivateFiles.TightenTree(DataDirectory);
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        VaultPath = Path.Combine(DataDirectory, "vault.xv");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        Settings = AppSettings.Load(SettingsPath);
        WalletRpcInstallRoot = Path.Combine(LocalDataRoot() ?? DataDirectory, "XaultWallet", "monero-cli");
    }

    /// <summary>%LOCALAPPDATA% on Windows (not the roaming profile: a 30 MB program has no business
    /// following the user between machines), ~/.local/share on Linux; null if it can't be determined.</summary>
    private static string? LocalDataRoot()
    {
        string root = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return Path.IsPathFullyQualified(root) ? root : null;
    }

    /// <summary>Where "Download &amp; install" puts monero-wallet-rpc: one folder per version.</summary>
    public string WalletRpcInstallRoot { get; }

    /// <summary>
    /// %APPDATA% on Windows, ~/Library/Application Support on macOS, $XDG_CONFIG_HOME or ~/.config on
    /// Linux — whether or not it exists yet. The default GetFolderPath option returns "" for a folder
    /// that doesn't exist (e.g. ~/.config on a fresh Linux account), and Path.Combine("", "XaultWallet")
    /// is RELATIVE: the vault would then be created in, and only found from, the launch directory.
    /// </summary>
    internal static string UserConfigRoot()
    {
        string root = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException(
                "Can't determine your user profile folder (is HOME set?), so there is nowhere safe to keep the vault.");
        }

        return root;
    }

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string VaultPath { get; set; }

    public string SettingsPath { get; }

    public AppSettings Settings { get; }

    /// <summary>What monero-wallet-rpc resolves to when the user hasn't set an explicit path
    /// (empty when it is neither next to the app, on PATH, nor installed by the app).</summary>
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

    /// <summary>Backends started for a moment outside an open wallet (seed generation, import checks).</summary>
    public TemporaryBackends TemporaryBackends { get; } = new();

    private string ResolveDefaultWalletRpcBinary()
    {
        string exe = ExecutableLocator.WalletRpcFileName;

        // The user's own copy first — next to our binary, then on PATH — then one the in-app
        // installer put in place. Always an absolute path, because the launcher (rightly) refuses
        // bare names.
        string local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local))
        {
            return local;
        }

        return ExecutableLocator.FindOnPath(exe, Environment.GetEnvironmentVariable("PATH"))
               ?? XaultWallet.Core.Installer.WalletRpcInstaller.FindInstalled(WalletRpcInstallRoot)
               ?? string.Empty;
    }
}
