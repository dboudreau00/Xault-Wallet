using System.Text.Json;

namespace XaultWallet.Desktop;

/// <summary>
/// Non-secret, user-configurable application settings (binary path, default daemon,
/// refresh cadence). Persisted as plain JSON in the data directory — it holds nothing
/// sensitive, so it is deliberately NOT part of the encrypted vault.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Explicit path to monero-wallet-rpc. Empty => auto-resolve (bundled dir, then PATH).</summary>
    public string WalletRpcBinaryPath { get; set; } = "";

    /// <summary>Pre-fills the daemon field when creating a wallet.</summary>
    public string DefaultDaemonAddress { get; set; } = "http://127.0.0.1:18081";

    /// <summary>0 = Mainnet, 1 = Stagenet, 2 = Testnet.</summary>
    public int DefaultNetworkIndex { get; set; }

    /// <summary>How often the open wallet re-polls balance/height/history.</summary>
    public int AutoRefreshSeconds { get; set; } = 20;

    /// <summary>Lock the wallet after this many minutes with no input. 0 = never.</summary>
    public int AutoLockMinutes { get; set; } = 10;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch
        {
            // A corrupt settings file must never block startup; fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save(string path)
    {
        Clamp();
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            if (File.Exists(path))
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch
        {
            try { if (File.Exists(tmp)) { File.Delete(tmp); } } catch { /* ignore */ }
            throw;
        }
    }

    private void Clamp()
    {
        WalletRpcBinaryPath ??= "";
        DefaultDaemonAddress ??= "";
        if (DefaultNetworkIndex is < 0 or > 2)
        {
            DefaultNetworkIndex = 0;
        }

        AutoRefreshSeconds = Math.Clamp(AutoRefreshSeconds, 5, 600);
        if (AutoLockMinutes != 0)
        {
            AutoLockMinutes = Math.Clamp(AutoLockMinutes, 1, 240);
        }
    }
}
