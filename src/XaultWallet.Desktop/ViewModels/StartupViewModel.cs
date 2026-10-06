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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContinue))]
    private bool _canContinue;      // becomes true if checks fail, so the user can proceed anyway
    [ObservableProperty] private bool _checking = true;

    private readonly CancellationTokenSource _cts = new();

    /// <summary>No working monero-wallet-rpc was found: the screen asks the user to choose their own
    /// copy or install one, instead of carrying on into a wallet that can't open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContinue))]
    private bool _needsBackend;

    /// <summary>The plain "Continue anyway" (the setup card has its own way out).</summary>
    public bool ShowContinue => CanContinue && !NeedsBackend;

    /// <summary>Feedback for "Choose my copy…" (e.g. the chosen file isn't monero-wallet-rpc).</summary>
    [ObservableProperty] private string _chooseResult = string.Empty;

    /// <summary>"Download &amp; install" for monero-wallet-rpc.</summary>
    public WalletRpcSetupViewModel Setup { get; } = new();

    /// <summary>Set by the View: a file picker returning the chosen path (or null).</summary>
    public Func<Task<string?>>? BrowseHandler { get; set; }

    // Completed once a working wallet-rpc is configured (installed or chosen) while NeedsBackend.
    private readonly TaskCompletionSource _backendReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised when startup is finished and the app should move to the next screen.</summary>
    public event Action? Ready;

    public StartupViewModel()
    {
        Setup.Installed += installed => _ = ContinueAfterInstallAsync();
        _ = RunAsync();
    }

    /// <summary>A splash that runs no checks — for UI snapshots and tests only.</summary>
    internal StartupViewModel(bool preview)
    {
        _ = preview;
    }

    private async Task RunAsync()
    {
        try
        {
            // 1. Locate the wallet RPC binary (reads Settings under the hood).
            Status = "Locating monero-wallet-rpc\u2026";
            string binary = AppServices.Instance.WalletRpcBinaryPath;
            try
            {
                string version = await MoneroDiagnostics.ProbeWalletRpcAsync(binary, _cts.Token);
                Detail = version;
                Log.Info("Startup: wallet-rpc OK — " + version);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Startup: wallet-rpc check failed — " + ex.GetType().Name);

                // Every wallet needs it: stop here and offer to set it up, rather than let the user
                // walk into a wallet screen that can only say "not found".
                Checking = false;
                Status = "XaultWallet needs monero-wallet-rpc";
                Detail = string.Empty;
                NeedsBackend = true;
                CanContinue = true; // they may still want to look around (or set it later in Settings)
                await _backendReady.Task.WaitAsync(_cts.Token);
                NeedsBackend = false;
                Checking = true;
                Detail = "monero-wallet-rpc is ready.";
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
                        ulong height = await MoneroDiagnostics.ProbeDaemonAsync(daemon, AppServices.Instance.Settings.ProxyAddress, _cts.Token);
                        Detail = $"Node reachable \u00b7 block {height:N0}";
                        Log.Info($"Startup: node OK at height {height}");
                        break;
                    }
                    catch
                    {
                        Detail = $"Waiting for node at {daemon}\u2026 (attempt {attempt + 1}/5)";
                        CanContinue = true; // don't make the user wait out all retries
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

    private int _finished; // once-only: Continue racing RunAsync's own Finish must not fire Ready twice

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        Checking = false;
        Status = "Ready";
        Ready?.Invoke();
        // _cts is deliberately NOT disposed here: Continue can race RunAsync, which still
        // touches _cts.Token afterwards — a disposed CTS there would turn a benign skip into
        // a logged startup error. One undisposed CTS per launch is the cheaper end of that trade.
    }

    /// <summary>Leave "Installed … verified" on screen for a moment before moving on, so the user
    /// sees what happened instead of the card just vanishing.</summary>
    private async Task ContinueAfterInstallAsync()
    {
        try
        {
            await Task.Delay(2000, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // Continue was clicked meanwhile; it already moved on
        }

        _backendReady.TrySetResult();
    }

    /// <summary>"Choose my copy…": the user's own monero-wallet-rpc (the recommended route). It must
    /// answer --version like monero-wallet-rpc before it is saved.</summary>
    [RelayCommand]
    private async Task ChooseOwnCopyAsync()
    {
        if (BrowseHandler is null)
        {
            return;
        }

        string? picked = await BrowseHandler();
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        ChooseResult = "Checking it\u2026";
        try
        {
            string version = await MoneroDiagnostics.ProbeWalletRpcAsync(picked, _cts.Token);
            AppServices.Instance.Settings.WalletRpcBinaryPath = picked;
            AppServices.Instance.SaveSettings();
            ChooseResult = string.Empty;
            Log.Info("Startup: wallet-rpc chosen by the user — " + version);
            _backendReady.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            // app closing
        }
        catch (Exception ex)
        {
            ChooseResult = ex.Message;
        }
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
