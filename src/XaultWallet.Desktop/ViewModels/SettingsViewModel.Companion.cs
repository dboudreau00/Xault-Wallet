using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XaultWallet.Core.Companion;
using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Security;

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
    [ObservableProperty] private bool _chromeCanDisconnect;
    [ObservableProperty] private string _firefoxStatus = string.Empty;
    [ObservableProperty] private bool _firefoxConnected;
    [ObservableProperty] private bool _firefoxElsewhere;
    [ObservableProperty] private bool _firefoxCanDisconnect;
    [ObservableProperty] private string _linksStatus = string.Empty;
    [ObservableProperty] private bool _linksRegistered;
    [ObservableProperty] private bool _linksElsewhere;
    [ObservableProperty] private bool _linksCanStop;
    [ObservableProperty] private string _companionResult = string.Empty;
    [ObservableProperty] private bool _companionOk;

    /// <summary>Saved with the other settings: label browser addresses with the site (off by default).</summary>
    [ObservableProperty] private bool _companionSavesSiteNames;

    /// <summary>Browsers can only be pointed at a real executable (not a development run through dotnet).</summary>
    public bool CanConnectBrowsers => AppServices.ExecutablePath is not null;

    public bool LinksSupported => MoneroLinkRegistration.IsSupported && CanConnectBrowsers;

    private void InitializeCompanion()
    {
        EnhancedSecurity = AppServices.Instance.Settings.EnhancedSecurity;
        CompanionSavesSiteNames = AppServices.Instance.Settings.CompanionSavesSiteNames;
        RefreshCompanion();
    }

    /// <summary>Read the registrations again (what the browsers would find right now).</summary>
    private void RefreshCompanion()
    {
        AppServices app = AppServices.Instance;
        string exe = AppServices.ExecutablePath ?? string.Empty;
        (ChromeStatus, ChromeConnected, ChromeElsewhere, ChromeCanDisconnect) = DescribeBrowser(CompanionBrowser.Chrome, "chrome", exe);
        (FirefoxStatus, FirefoxConnected, FirefoxElsewhere, FirefoxCanDisconnect) = DescribeBrowser(CompanionBrowser.Firefox, "firefox", exe);

        RegistrationState links = exe.Length == 0 ? RegistrationState.Unsupported : MoneroLinkRegistration.Status(exe, app.BrowserPaths, app.UserRegistry);
        bool watched = app.Settings.OpensMoneroLinks;
        (LinksStatus, LinksRegistered, LinksElsewhere) = links switch
        {
            RegistrationState.Registered => ("Registered", true, false),
            RegistrationState.Elsewhere => ("Another program opens them", false, true),
            RegistrationState.NotRegistered when watched => ("Removed outside XaultWallet", false, true),
            RegistrationState.NotRegistered => ("Not registered", false, false),
            _ => ("Not available here", false, false),
        };
        LinksCanStop = links == RegistrationState.Registered || watched;
        if (links == RegistrationState.Registered && MoneroLinkRegistration.WindowsUserChoiceOverrides(app.UserRegistry))
        {
            LinksStatus = "Registered, but Windows is set to open them with another app (Settings, Apps, Default apps)";
            LinksElsewhere = true;
        }
    }

    /// <summary>A browser's row: its status text, whether it leads here, whether it leads elsewhere (or
    /// was removed behind the app's back), and whether Disconnect has something to do.</summary>
    private static (string text, bool on, bool elsewhere, bool canDisconnect) DescribeBrowser(CompanionBrowser browser, string key, string exe)
    {
        if (exe.Length == 0)
        {
            return ("Not available here", false, false, false);
        }

        AppServices app = AppServices.Instance;
        bool watched = app.Settings.ConnectedBrowsers.Contains(key);
        RegistrationCheck check = BrowserRegistration.Check(browser, exe, app.BrowserPaths, app.UserRegistry);
        return check.State switch
        {
            RegistrationState.Registered => ("Connected to this XaultWallet", true, false, true),
            RegistrationState.Elsewhere => ("Leads to another program" + (check.Target is { } target ? ": " + UntrustedText.Clean(target, 80) : string.Empty), false, true, true),
            RegistrationState.NotRegistered when watched => ("Removed outside XaultWallet", false, true, true),
            RegistrationState.NotRegistered => ("Not connected", false, false, false),
            _ => ("Not available here", false, false, false),
        };
    }

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
        bool wasWatched = app.Settings.ConnectedBrowsers.Contains(which);
        try
        {
            if (connect)
            {
                // Watched first: a registration made but not saved as watched would be one the tripwire never checks.
                if (!wasWatched)
                {
                    app.Settings.ConnectedBrowsers.Add(which);
                    app.SaveSettings();
                }

                BrowserRegistration.Register(browser, exe, app.BrowserPaths, app.UserRegistry);
                bool connected = BrowserRegistration.Status(browser, exe, app.BrowserPaths, app.UserRegistry) == RegistrationState.Registered;
                CompanionOk = connected;
                CompanionResult = connected
                    ? $"{name} is connected. Install the XaultWallet companion extension in {name} (see the README), then use it from its toolbar button."
                    : $"{name}'s connection was written, but reading it back doesn't lead to this XaultWallet. Try again, or check what changes {name}'s settings.";
            }
            else
            {
                // Removes this app's registration, or one another program put in its place.
                BrowserRegistration.Unregister(browser, app.BrowserPaths, app.UserRegistry);
                app.Settings.ConnectedBrowsers.Remove(which);
                app.SaveSettings();
                CompanionOk = true;
                CompanionResult = $"{name} is disconnected: its extension can no longer reach XaultWallet.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.Security.SecurityException or InvalidDataException)
        {
            if (connect && !wasWatched && BrowserRegistration.Status(browser, exe, app.BrowserPaths, app.UserRegistry) == RegistrationState.NotRegistered)
            {
                app.Settings.ConnectedBrowsers.Remove(which); // nothing was registered after all
                TrySave(app);
            }

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
                // Say what is replaced: it may be another wallet the user still wants.
                string? before = MoneroLinkRegistration.Status(exe, app.BrowserPaths, app.UserRegistry) == RegistrationState.Elsewhere
                    ? MoneroLinkRegistration.CurrentHandler(app.BrowserPaths, app.UserRegistry)
                    : null;
                app.Settings.OpensMoneroLinks = true;
                app.SaveSettings();
                MoneroLinkRegistration.Register(exe, app.BrowserPaths, app.UserRegistry);
                CompanionResult = "monero: links now open in XaultWallet. A payment request waits in the Send tab for you to review; nothing is sent without your confirmation." +
                                  (before is { Length: > 0 } ? " This replaced: " + UntrustedText.Clean(before, 120) + "." : string.Empty);
            }
            else
            {
                MoneroLinkRegistration.Unregister(exe, app.BrowserPaths, app.UserRegistry); // only this copy's
                app.Settings.OpensMoneroLinks = false;
                app.SaveSettings();
                CompanionResult = "monero: links no longer open in XaultWallet.";
            }

            CompanionOk = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException or System.Security.SecurityException or InvalidDataException)
        {
            if (register && MoneroLinkRegistration.Status(exe, app.BrowserPaths, app.UserRegistry) != RegistrationState.Registered)
            {
                app.Settings.OpensMoneroLinks = false;
                TrySave(app);
            }

            CompanionOk = false;
            CompanionResult = "Couldn't change what opens monero: links: " + ex.Message;
            Log.Error("Changing the monero: link handler failed", ex);
        }

        RefreshCompanion();
    }

    private static void TrySave(AppServices app)
    {
        try
        {
            app.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings failed", ex);
        }
    }

    /// <summary>Part of Save: Enhanced Security is stored like any setting, then the shell applies it.
    /// Turned on, it starts unconfirmed: it starts with the app only once the user has clicked Keep it on.</summary>
    private void SaveEnhancedSecurity(AppSettings s, out bool changed)
    {
        bool before = s.EnhancedSecurity;
        s.EnhancedSecurity = EnhancedSecurity && EnhancedSecuritySupported;
        changed = before != s.EnhancedSecurity;
        if (changed)
        {
            s.EnhancedSecurityConfirmed = false;
        }
    }

    private void AfterSave(bool enhancedChanged)
    {
        if (enhancedChanged)
        {
            EnhancedSecurityChanged?.Invoke(AppServices.Instance.Settings.EnhancedSecurity);
        }
    }
}
