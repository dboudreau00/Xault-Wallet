using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Monero;

namespace XaultWallet.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private string _walletRpcBinaryPath;
    [ObservableProperty] private string _defaultDaemonAddress;
    [ObservableProperty] private int _networkIndex;
    [ObservableProperty] private int _autoRefreshSeconds;

    [ObservableProperty] private string _binaryTestResult = string.Empty;
    [ObservableProperty] private bool _binaryTestOk;
    [ObservableProperty] private string _daemonTestResult = string.Empty;
    [ObservableProperty] private bool _daemonTestOk;
    [ObservableProperty] private string _savedMessage = string.Empty;
    [ObservableProperty] private bool _busy;

    /// <summary>Selecting a preset fills the daemon address and network below.</summary>
    [ObservableProperty] private RemoteNode? _selectedPreset;

    public IReadOnlyList<RemoteNode> PresetNodes => RemoteNodes.All;

    partial void OnSelectedPresetChanged(RemoteNode? value)
    {
        if (value is null)
        {
            return;
        }

        DefaultDaemonAddress = value.Url;
        NetworkIndex = value.NetworkIndex;
        SavedMessage = string.Empty;
        DaemonTestResult = string.Empty;
    }

    /// <summary>Set by the View: opens a file picker and returns the chosen path (or null).</summary>
    public Func<Task<string?>>? BrowseHandler { get; set; }

    public event Action? Closed;

    public string DefaultBinaryHint { get; }

    public SettingsViewModel()
    {
        AppSettings s = AppServices.Instance.Settings;
        _walletRpcBinaryPath = s.WalletRpcBinaryPath;
        _defaultDaemonAddress = s.DefaultDaemonAddress;
        _networkIndex = s.DefaultNetworkIndex;
        _autoRefreshSeconds = s.AutoRefreshSeconds;
        DefaultBinaryHint = "Leave blank to auto-detect. Currently resolves to: " +
                            AppServices.Instance.ResolvedDefaultWalletRpcBinary;
    }

    partial void OnWalletRpcBinaryPathChanged(string value) => SavedMessage = string.Empty;
    partial void OnDefaultDaemonAddressChanged(string value) => SavedMessage = string.Empty;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (BrowseHandler is null)
        {
            return;
        }

        string? picked = await BrowseHandler();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            WalletRpcBinaryPath = picked;
        }
    }

    [RelayCommand]
    private async Task TestBinaryAsync()
    {
        Busy = true;
        BinaryTestOk = false;
        BinaryTestResult = "Testing\u2026";
        try
        {
            string path = string.IsNullOrWhiteSpace(WalletRpcBinaryPath)
                ? AppServices.Instance.ResolvedDefaultWalletRpcBinary
                : WalletRpcBinaryPath.Trim();

            string version = await MoneroDiagnostics.ProbeWalletRpcAsync(path);
            BinaryTestOk = true;
            BinaryTestResult = "OK \u2014 " + version;
        }
        catch (Exception ex)
        {
            BinaryTestOk = false;
            BinaryTestResult = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task TestDaemonAsync()
    {
        Busy = true;
        DaemonTestOk = false;
        DaemonTestResult = "Contacting daemon\u2026";
        try
        {
            ulong height = await MoneroDiagnostics.ProbeDaemonAsync(DefaultDaemonAddress);
            DaemonTestOk = true;
            DaemonTestResult = $"OK \u2014 daemon at height {height}.";
        }
        catch (Exception ex)
        {
            DaemonTestOk = false;
            DaemonTestResult = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            AppSettings s = AppServices.Instance.Settings;
            s.WalletRpcBinaryPath = (WalletRpcBinaryPath ?? string.Empty).Trim();
            s.DefaultDaemonAddress = (DefaultDaemonAddress ?? string.Empty).Trim();
            s.DefaultNetworkIndex = NetworkIndex;
            s.AutoRefreshSeconds = AutoRefreshSeconds;
            AppServices.Instance.SaveSettings();

            // Reflect any clamping back into the fields.
            AutoRefreshSeconds = s.AutoRefreshSeconds;
            SavedMessage = "Settings saved.";
            Log.Info("Settings saved.");
        }
        catch (Exception ex)
        {
            SavedMessage = "Couldn't save settings: " + ex.Message;
            Log.Error("Saving settings failed", ex);
        }
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
