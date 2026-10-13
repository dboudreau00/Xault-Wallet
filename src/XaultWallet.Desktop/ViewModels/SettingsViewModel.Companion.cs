using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Companion;
using XaultWallet.Core.Diagnostics;

namespace XaultWallet.Desktop.ViewModels;

/// <summary>Settings: the browser companion (connect Chrome and Firefox, open monero: links) and
/// Enhanced Security.</summary>
public sealed partial class SettingsViewModel
{
    // ------------------------------------------------------------------ Enhanced Security

    /// <summary>Saved with the other settings; applied (and confirmed) by the shell.</summary>
    [ObservableProperty] private bool _enhancedSecurity;

    public bool EnhancedSecuritySupported => XaultWallet.Desktop.EnhancedSecurity.IsSupported;

    /// <summary>Raised after a save that turned Enhanced Security on (true) or off (false).</summary>
    public event Action<bool>? EnhancedSecurityChanged;

    // ------------------------------------------------------------------ browser companion

    [ObservableProperty] private string _chromeStatus = string.Empty;
    [ObservableProperty] private bool _chromeConnected;
    [ObservableProperty] private bool _chromeElsewhere;
    [ObservableProperty] private string _firefoxStatus = string.Empty;
    [ObservableProperty] private bool _firefoxConnected;
    [ObservableProperty] private bool _firefoxElsewhere;
    [ObservableProperty] private string _linksStatus = string.Empty;
    [ObservableProperty] private bool _linksRegistered;
    [ObservableProperty] private bool _linksElsewhere;
    [ObservableProperty] private string _companionResult = string.Empty;
    [ObservableProperty] private bool _companionOk;

    /// <summary>Browsers can only be pointed at a real executable (not a development run through dotnet).</summary>
    public bool CanConnectBrowsers => AppServices.ExecutablePath is not null;

    public bool LinksSupported => MoneroLinkRegistration.IsSupported && CanConnectBrowsers;

    private void InitializeCompanion()
    {
        EnhancedSecurity = AppServices.Instance.Settings.EnhancedSecurity;
        RefreshCompanion();
    }

    /// <summary>Read the registrations again (what the browsers would find right now).</summary>
    private void RefreshCompanion()
    {
        AppServices app = AppServices.Instance;
        string exe = AppServices.ExecutablePath ?? string.Empty;
        (ChromeStatus, ChromeConnected, ChromeElsewhere) = Describe(exe.Length == 0 ? RegistrationState.Unsupported : BrowserRegistration.Status(CompanionBrowser.Chrome, exe, app.BrowserPaths, app.UserRegistry));
        (FirefoxStatus, FirefoxConnected, FirefoxElsewhere) = Describe(exe.Length == 0 ? RegistrationState.Unsupported : BrowserRegistration.Status(CompanionBrowser.Firefox, exe, app.BrowserPaths, app.UserRegistry));
        RegistrationState links = exe.Length == 0 ? RegistrationState.Unsupported : MoneroLinkRegistration.Status(exe, app.BrowserPaths, app.UserRegistry);
        (LinksStatus, LinksRegistered, LinksElsewhere) = Describe(links);
        if (links == RegistrationState.Registered && MoneroLinkRegistration.WindowsUserChoiceOverrides(app.UserRegistry))
        {
            LinksStatus = "Registered, but Windows is set to open them with another app (Settings, Apps, Default apps)";
            LinksElsewhere = true;
        }
    }

    private static (string text, bool on, bool elsewhere) Describe(RegistrationState state) => state switch
    {
        RegistrationState.Registered => ("Connected to this XaultWallet", true, false),
        RegistrationState.Elsewhere => ("Leads to another program", false, true),
        RegistrationState.NotRegistered => ("Not connected", false, false),
        _ => ("Not available here", false, false),
    };

    [RelayCommand]
    private void ConnectBrowser(string? which) => ChangeBrowser(which, connect: true);

    [RelayCommand]
    private void DisconnectBrowser(string? which) => ChangeBrowser(which, connect: false);

    private void ChangeBrowser(string? which, bool connect)
    {
        if (which is not ("chrome" or "firefox") || AppServices.ExecutablePath is not { } exe)
        {
            return;
        }

        AppServices app = AppServices.Instance;
        CompanionBrowser browser = which == "chrome" ? CompanionBrowser.Chrome : CompanionBrowser.Firefox;
        string name = browser.ToString();
        try
        {
            if (connect)
            {
                BrowserRegistration.Register(browser, exe, app.BrowserPaths, app.UserRegistry);
                if (!app.Settings.ConnectedBrowsers.Contains(which))
                {
                    app.Settings.ConnectedBrowsers.Add(which);
                }

                CompanionResult = $"{name} is connected. Install the XaultWallet companion extension in {name} (see the README), then use it from its toolbar button.";
            }
            else
            {
                BrowserRegistration.Unregister(browser, app.BrowserPaths, app.UserRegistry);
                app.Settings.ConnectedBrowsers.Remove(which);
                CompanionResult = $"{name} is disconnected: its extension can no longer reach XaultWallet.";
            }

            app.SaveSettings();
            CompanionOk = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            CompanionOk = false;
            CompanionResult = $"Couldn't change the {name} connection: {ex.Message}";
            Log.Error("Changing a browser connection failed", ex);
        }

        RefreshCompanion();
    }

    [RelayCommand]
    private void RegisterLinks() => ChangeLinks(register: true);

    [RelayCommand]
    private void UnregisterLinks() => ChangeLinks(register: false);

    private void ChangeLinks(bool register)
    {
        if (AppServices.ExecutablePath is not { } exe)
        {
            return;
        }

        AppServices app = AppServices.Instance;
        try
        {
            if (register)
            {
                MoneroLinkRegistration.Register(exe, app.BrowserPaths, app.UserRegistry);
                CompanionResult = "monero: links now open in XaultWallet. A payment request waits in the Send tab for you to review; nothing is sent without your confirmation.";
            }
            else
            {
                MoneroLinkRegistration.Unregister(exe, app.BrowserPaths, app.UserRegistry);
                CompanionResult = "monero: links no longer open in XaultWallet.";
            }

            app.Settings.OpensMoneroLinks = register;
            app.SaveSettings();
            CompanionOk = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            CompanionOk = false;
            CompanionResult = "Couldn't change what opens monero: links: " + ex.Message;
            Log.Error("Changing the monero: link handler failed", ex);
        }

        RefreshCompanion();
    }

    /// <summary>Part of Save: Enhanced Security is stored like any setting, then the shell applies it.</summary>
    private void SaveEnhancedSecurity(AppSettings s, out bool changed)
    {
        bool before = s.EnhancedSecurity;
        s.EnhancedSecurity = EnhancedSecurity && EnhancedSecuritySupported;
        changed = before != s.EnhancedSecurity;
    }

    private void AfterSave(bool enhancedChanged)
    {
        if (enhancedChanged)
        {
            EnhancedSecurityChanged?.Invoke(AppServices.Instance.Settings.EnhancedSecurity);
        }
    }
}
