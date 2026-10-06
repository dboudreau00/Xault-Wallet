using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Installer;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>
/// "Download &amp; install" for monero-wallet-rpc, shared by the startup screen and Settings. Wraps
/// <see cref="WalletRpcInstaller"/>: progress, cancel, and a plain result. On success the installed
/// path becomes the configured one, so the app uses exactly what was just verified.
/// </summary>
public sealed partial class WalletRpcSetupViewModel : ViewModelBase
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _installing;

    /// <summary>0–100 while the size is known; see <see cref="ProgressKnown"/>.</summary>
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressKnown;
    [ObservableProperty] private string _stageText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultText = string.Empty;

    [ObservableProperty] private bool _succeeded;

    public bool HasResult => ResultText.Length > 0;

    public bool CanInstall => !Installing;

    /// <summary>Raised on the UI thread after a verified install.</summary>
    public event Action<InstalledWalletRpc>? Installed;

    /// <summary>Which route the download takes — the same one the wallet's own traffic takes.</summary>
    public string RouteNote
    {
        get
        {
            string proxy = AppServices.Instance.Settings.ProxyAddress.Trim();
            return proxy.Length > 0
                ? $"The download goes through your proxy ({proxy}), like the wallet's own traffic."
                : "It connects to getmonero.org directly. Set a SOCKS proxy (e.g. Tor) under Network & privacy first if you'd rather it didn't.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        Installing = true;
        Succeeded = false;
        ResultText = string.Empty;
        Progress = 0;
        ProgressKnown = false;
        _cts = new CancellationTokenSource();
        try
        {
            var installer = new WalletRpcInstaller(AppServices.Instance.WalletRpcInstallRoot, AppServices.Instance.Settings.ProxyAddress);
            var progress = new Progress<InstallProgress>(Report); // created on the UI thread: reports land there
            InstalledWalletRpc installed = await installer.InstallAsync(progress, _cts.Token);

            AppServices.Instance.Settings.WalletRpcBinaryPath = installed.Path;
            AppServices.Instance.SaveSettings();

            Succeeded = true;
            ResultText = $"Installed monero-wallet-rpc {installed.Version}. binaryFate's signature and the download's " +
                         "checksum were verified, and it ran on this system.";
            Log.Info("Installed monero-wallet-rpc " + installed.Version + " (signature and checksum verified).");
            Installed?.Invoke(installed);
        }
        catch (OperationCanceledException)
        {
            ResultText = "Cancelled. Nothing was installed.";
        }
        catch (WalletRpcInstallException ex)
        {
            ResultText = ex.Message;
            Log.Warn("monero-wallet-rpc install failed: " + ex.Message);
        }
        catch (Exception ex)
        {
            ResultText = "The install failed: " + ex.Message;
            Log.Error("monero-wallet-rpc install failed", ex);
        }
        finally
        {
            StageText = string.Empty;
            Installing = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Report(InstallProgress p)
    {
        ProgressKnown = p.Stage == InstallStage.Downloading && p.BytesTotal > 0;
        if (ProgressKnown)
        {
            Progress = 100.0 * p.BytesDone / p.BytesTotal;
        }

        StageText = p.Stage switch
        {
            InstallStage.FetchingList => "Getting Monero's signed release list from getmonero.org…",
            InstallStage.CheckingSignature => "Checking binaryFate's signature on it…",
            InstallStage.Downloading => p.BytesTotal > 0
                ? $"Downloading the official Monero CLI… {Mb(p.BytesDone)} of {Mb(p.BytesTotal)} MB"
                : $"Downloading the official Monero CLI… {Mb(p.BytesDone)} MB",
            InstallStage.CheckingChecksum => "Checking the download against the signed checksum…",
            InstallStage.Extracting => "Unpacking monero-wallet-rpc…",
            InstallStage.Testing => "Running it once to make sure it works here…",
            _ => StageText,
        };
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
}
