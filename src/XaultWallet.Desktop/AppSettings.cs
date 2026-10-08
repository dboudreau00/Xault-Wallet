using System.Text.Json;
using XaultWallet.Core.Security;

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
    public string DefaultDaemonAddress { get; set; } = "http://127.0.0.1:38081";

    /// <summary>0 = Mainnet, 1 = Stagenet, 2 = Testnet. Defaults to STAGENET: this is unaudited beta
    /// software, and a first wallet should never default to real funds. Mainnet stays one click away
    /// (behind its warning) for those who choose it.</summary>
    public int DefaultNetworkIndex { get; set; } = 1;

    /// <summary>How often the open wallet re-polls balance/height/history.</summary>
    public int AutoRefreshSeconds { get; set; } = 20;

    /// <summary>Lock the wallet after this many minutes with no input. 0 = never.</summary>
    public int AutoLockMinutes { get; set; } = 10;

    /// <summary>Mask balances on the wallet screen until the user reveals them.</summary>
    public bool HideBalances { get; set; }

    /// <summary>Optional SOCKS proxy for the wallet backend's daemon traffic (e.g.
    /// "127.0.0.1:9050" for Tor). Empty = direct connection. Passed to monero-wallet-rpc
    /// as --proxy so the configured node never sees the user's real IP.</summary>
    public string ProxyAddress { get; set; } = "";

    /// <summary>Route node traffic (and the in-app downloads) through the app's own Tor. While on,
    /// nothing goes direct: until Tor is connected, node traffic waits. Overrides <see cref="ProxyAddress"/>.
    /// A node on this computer (127.0.0.1) is reached directly: Tor can't, and nothing leaves the machine.</summary>
    public bool UseBuiltInTor { get; set; }

    /// <summary>Explicit tor binary. Empty = the one XaultWallet installed, else tor on PATH.</summary>
    public string TorBinaryPath { get; set; } = "";

    /// <summary>The main window as it was last closed (0 = never saved). Not secret, and the same for
    /// every password, so it says nothing about which one was used.</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>True when the last Load found a corrupt settings file and fell back to
    /// defaults (the corrupt file is preserved next to the original as *.bad).</summary>
    public static bool RecoveredFromCorruptFile { get; private set; }

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
            // A corrupt settings file must never block startup; fall back to defaults —
            // but don't silently discard the user's node/binary configuration: keep the
            // corrupt file for inspection and let the UI mention what happened.
            RecoveredFromCorruptFile = true;
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* best effort */ }
        }

        return new AppSettings();
    }

    public void Save(string path)
    {
        Clamp();
        RecoveredFromCorruptFile = false; // a successful save writes a valid file again
        string tmp = path + ".tmp";
        try
        {
            PrivateFiles.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
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
        ProxyAddress ??= "";
        TorBinaryPath ??= "";
        if (WindowWidth is < 0 or > 20000 || double.IsNaN(WindowWidth) || WindowHeight is < 0 or > 20000 || double.IsNaN(WindowHeight))
        {
            WindowWidth = WindowHeight = 0;
        }
        if (DefaultNetworkIndex is < 0 or > 2)
        {
            DefaultNetworkIndex = 1;
        }

        AutoRefreshSeconds = Math.Clamp(AutoRefreshSeconds, 5, 600);
        if (AutoLockMinutes != 0)
        {
            AutoLockMinutes = Math.Clamp(AutoLockMinutes, 1, 240);
        }
    }
}
