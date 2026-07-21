using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>
/// First screen shown at launch. While the X mark is on screen it checks, in the background,
/// that the monero-wallet-rpc binary is present and that the configured node is reachable,
/// then hands off to either the create-wallet flow (no vault yet) or the unlock screen.
///
/// It does NOT start monerod: the daemon is a long-lived process managed outside the wallet.
/// This screen waits for it and reports status.
/// </summary>
public sealed partial class StartupViewModel : ViewModelBase
{
    [ObservableProperty] private string _status = "Starting XaultWallet\u2026";
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private bool _canContinue;      // becomes true if checks fail, so the user can proceed anyway
    [ObservableProperty] private bool _checking = true;

    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised when startup is finished and the app should move to the next screen.</summary>
    public event Action? Ready;

    public StartupViewModel() => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            // 1. Locate the wallet RPC binary (reads Settings under the hood).
            Status = "Locating monero-wallet-rpc\u2026";
            string binary = AppServices.Instance.WalletRpcBinaryPath;
            bool binaryOk = false;
            try
            {
                string version = await MoneroDiagnostics.ProbeWalletRpcAsync(binary, _cts.Token);
                binaryOk = true;
                Detail = version;
                Log.Info("Startup: wallet-rpc OK — " + version);
            }
            catch (Exception ex)
            {
                Detail = "monero-wallet-rpc not found. You can set it in Settings.";
                Log.Warn("Startup: wallet-rpc check failed — " + ex.GetType().Name);
            }

            // 2. Probe the configured node (waits for monerod, does not start it).
            string daemon = AppServices.Instance.DefaultDaemonAddress;
            if (!string.IsNullOrWhiteSpace(daemon))
            {
                Status = "Contacting your node\u2026";
                for (int attempt = 0; attempt < 5 && !_cts.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        ulong height = await MoneroDiagnostics.ProbeDaemonAsync(daemon, _cts.Token);
                        Detail = $"Node reachable \u00b7 block {height:N0}";
                        Log.Info($"Startup: node OK at height {height}");
                        break;
                    }
                    catch
                    {
                        Detail = $"Waiting for node at {daemon}\u2026 (attempt {attempt + 1}/5)";
                        await Task.Delay(1500, _cts.Token);
                    }
                }
            }

            // Brief beat so the splash doesn't flash by on a fast machine.
            await Task.Delay(500, _cts.Token);
            Finish();
        }
        catch (OperationCanceledException)
        {
            // app closing
        }
        catch (Exception ex)
        {
            Log.Error("Startup sequence error", ex);
            Finish();
        }
    }

    private void Finish()
    {
        Checking = false;
        Status = "Ready";
        Ready?.Invoke();
    }

    /// <summary>Lets the user skip straight in if a check is slow or a node isn't up yet.</summary>
    [RelayCommand]
    private void Continue()
    {
        _cts.Cancel();
        Finish();
    }

    public bool VaultExists => VaultManager.Exists(AppServices.Instance.VaultPath);
}
