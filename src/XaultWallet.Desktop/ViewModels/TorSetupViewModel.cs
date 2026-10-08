using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Installer;
using XaultWallet.Core.Tor;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>
/// "Download &amp; install Tor" in Settings: wraps <see cref="TorInstaller"/> with progress, cancel and
/// a plain result. The installed tor is then the one the built-in Tor runs (unless Settings names
/// another), and a running Tor is restarted onto it.
/// </summary>
public sealed partial class TorSetupViewModel : ViewModelBase
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _installing;

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
    public event Action<InstalledTor>? Installed;

    /// <summary>Which route the download takes. Tor can't fetch itself.</summary>
    public string RouteNote
    {
        get
        {
            string proxy = AppServices.Instance.Settings.ProxyAddress.Trim();
            return !AppServices.Instance.Settings.UseBuiltInTor && proxy.Length > 0
                ? $"The download goes through your proxy ({proxy})."
                : "It connects to torproject.org directly (Tor can't download itself). No node is contacted.";
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
            AppSettings s = AppServices.Instance.Settings;
            string? proxy = !s.UseBuiltInTor && s.ProxyAddress.Trim().Length > 0 ? s.ProxyAddress.Trim() : null;
            var installer = new TorInstaller(AppServices.Instance.TorInstallRoot, proxy);
            var progress = new Progress<InstallProgress>(Report); // created on the UI thread: reports land there
            InstalledTor installed = await installer.InstallAsync(progress, _cts.Token);

            Succeeded = true;
            ResultText = $"Installed Tor from Tor Browser {installed.Version} ({installed.VersionLine.TrimEnd('.')}). " +
                         "Tor Project's signature and the download's checksum were verified, and it ran on this system.";
            Log.Info("Installed Tor " + installed.Version + " (signature and checksum verified).");
            Installed?.Invoke(installed);
        }
        catch (OperationCanceledException)
        {
            ResultText = "Cancelled. Nothing was installed.";
        }
        catch (TorInstallException ex)
        {
            ResultText = ex.Message;
            Log.Warn("Tor install failed: " + ex.Message);
        }
        catch (Exception ex)
        {
            ResultText = "The install failed: " + ex.Message;
            Log.Error("Tor install failed", ex);
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
            InstallStage.FetchingList => "Asking torproject.org for the current release and its signed checksums…",
            InstallStage.CheckingSignature => "Checking the Tor Browser Developers' signature on them…",
            InstallStage.Downloading => p.BytesTotal > 0
                ? $"Downloading the Tor Expert Bundle… {Mb(p.BytesDone)} of {Mb(p.BytesTotal)} MB"
                : $"Downloading the Tor Expert Bundle… {Mb(p.BytesDone)} MB",
            InstallStage.CheckingChecksum => "Checking the download against the signed checksum…",
            InstallStage.Extracting => "Unpacking tor…",
            InstallStage.Testing => "Running it once to make sure it works here…",
            _ => StageText,
        };
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
}
