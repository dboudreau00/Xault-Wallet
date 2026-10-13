using System.Text.Json;
using XaultWallet.Core.Companion;
using Xunit;

namespace XaultWallet.Core.Tests.Companion;

/// <summary>A registry in memory: the tests never touch the real HKEY_CURRENT_USER.</summary>
internal sealed class FakeRegistry : IUserRegistry
{
    public Dictionary<string, Dictionary<string, string>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys whose values aren't text (a number, binary data): reading them throws, as the real one does.</summary>
    public HashSet<string> NotText { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetValue(string subKey, string name) =>
        NotText.Contains(subKey) ? throw new InvalidDataException("The registry value isn't text.")
        : Keys.TryGetValue(subKey, out Dictionary<string, string>? values) && values.TryGetValue(name, out string? v) ? v : null;

    public void SetValue(string subKey, string name, string value)
    {
        if (!Keys.TryGetValue(subKey, out Dictionary<string, string>? values))
        {
            Keys[subKey] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        values[name] = value;
    }

    public void DeleteTree(string subKey)
    {
        foreach (string key in Keys.Keys.Where(k => k.Equals(subKey, StringComparison.OrdinalIgnoreCase) || k.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Keys.Remove(key);
        }
    }
}

/// <summary>Connecting the browsers and opening monero: links, in a throwaway home folder.</summary>
public sealed class RegistrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xw-reg-").FullName;
    private readonly BrowserPaths _paths;
    private readonly FakeRegistry _registry = new();
    private readonly string _exe;

    public RegistrationTests()
    {
        _paths = new BrowserPaths(Path.Combine(_root, "home"), Path.Combine(_root, "config"), Path.Combine(_root, "data"), Path.Combine(_root, "local", "XaultWallet"));
        _exe = Path.Combine(_root, "app", OperatingSystem.IsWindows() ? "XaultWallet.exe" : "XaultWallet");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(CompanionBrowser.Chrome, "allowed_origins", "chrome-extension://kpcmnhbbahdifkhgibnboafelgfkgcdj/")]
    [InlineData(CompanionBrowser.Firefox, "allowed_extensions", "xaultwallet-companion@dboudreau.dev")]
    public void Manifests_Name_The_Executable_And_Only_Our_Extension(CompanionBrowser browser, string list, string allowed)
    {
        using JsonDocument doc = JsonDocument.Parse(BrowserRegistration.ManifestJson(browser, @"C:\Program Files\XaultWallet\XaultWallet.exe"));
        JsonElement m = doc.RootElement;
        Assert.Equal("dev.dboudreau.xaultwallet", m.GetProperty("name").GetString());
        Assert.Equal(@"C:\Program Files\XaultWallet\XaultWallet.exe", m.GetProperty("path").GetString());
        Assert.Equal("stdio", m.GetProperty("type").GetString());
        Assert.Equal(new[] { allowed }, m.GetProperty(list).EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(5, m.EnumerateObject().Count());
    }

    [Fact]
    public void The_Host_Name_Follows_The_Browsers_Rules() =>
        Assert.Matches("^[a-z0-9_]+(\\.[a-z0-9_]+)*$", BrowserRegistration.HostName);

    [Theory]
    [InlineData(CompanionBrowser.Chrome)]
    [InlineData(CompanionBrowser.Firefox)]
    public void Connect_Check_And_Disconnect(CompanionBrowser browser)
    {
        Assert.Equal(RegistrationState.NotRegistered, BrowserRegistration.Status(browser, _exe, _paths, _registry));

        BrowserRegistration.Register(browser, _exe, _paths, _registry);
        Assert.Equal(RegistrationState.Registered, BrowserRegistration.Status(browser, _exe, _paths, _registry));
        string manifest = BrowserRegistration.ManifestPath(browser, _paths)!;
        Assert.True(File.Exists(manifest));
        Assert.StartsWith(_root, manifest, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(manifest, _registry.GetValue(BrowserRegistration.RegistryKey(browser), string.Empty));
        }

        // Another copy (or anything else) taking the name over shows as "elsewhere".
        string other = Path.Combine(_root, "other", Path.GetFileName(_exe));
        BrowserRegistration.Register(browser, other, _paths, _registry);
        Assert.Equal(RegistrationState.Elsewhere, BrowserRegistration.Status(browser, _exe, _paths, _registry));

        BrowserRegistration.Register(browser, _exe, _paths, _registry);
        BrowserRegistration.Unregister(browser, _paths, _registry);
        Assert.Equal(RegistrationState.NotRegistered, BrowserRegistration.Status(browser, _exe, _paths, _registry));
        Assert.False(File.Exists(manifest));
        Assert.Empty(_registry.Keys);
    }

    [Fact]
    public void A_Manifest_Repointed_Behind_The_Apps_Back_Is_Noticed()
    {
        BrowserRegistration.Register(CompanionBrowser.Chrome, _exe, _paths, _registry);
        string manifest = BrowserRegistration.ManifestPath(CompanionBrowser.Chrome, _paths)!;
        File.WriteAllText(manifest, BrowserRegistration.ManifestJson(CompanionBrowser.Chrome, Path.Combine(_root, "evil", "host")));

        Assert.Equal(RegistrationState.Elsewhere, BrowserRegistration.Status(CompanionBrowser.Chrome, _exe, _paths, _registry));

        File.WriteAllText(manifest, "not json");
        Assert.Equal(RegistrationState.Elsewhere, BrowserRegistration.Status(CompanionBrowser.Chrome, _exe, _paths, _registry));
    }

    [Fact]
    public void Relative_Executables_Are_Refused() =>
        Assert.Throws<ArgumentException>(() => BrowserRegistration.Register(CompanionBrowser.Chrome, "XaultWallet.exe", _paths, _registry));

    /// <summary>Writes the manifest the browser will read (registered first, so on Windows the key names it).</summary>
    private string PlantManifest(string text)
    {
        BrowserRegistration.Register(CompanionBrowser.Chrome, _exe, _paths, _registry);
        string manifest = BrowserRegistration.ManifestPath(CompanionBrowser.Chrome, _paths)!;
        File.WriteAllText(manifest, text);
        return manifest;
    }

    private RegistrationState ChromeStatus() => BrowserRegistration.Status(CompanionBrowser.Chrome, _exe, _paths, _registry);

    [Fact]
    public void A_Relative_Or_Roundabout_Path_To_The_Same_Name_Is_Another_Program()
    {
        // Windows browsers resolve a relative path against the manifest's own folder, where anything could sit.
        PlantManifest(BrowserRegistration.ManifestJson(CompanionBrowser.Chrome, Path.GetFileName(_exe)));
        Assert.Equal(RegistrationState.Elsewhere, ChromeStatus());

        string roundabout = Path.Combine(_root, "app", "..", "app", Path.GetFileName(_exe));
        PlantManifest(BrowserRegistration.ManifestJson(CompanionBrowser.Chrome, roundabout));
        Assert.Equal(RegistrationState.Elsewhere, ChromeStatus());

        PlantManifest(BrowserRegistration.ManifestJson(CompanionBrowser.Chrome, _exe));
        Assert.Equal(RegistrationState.Registered, ChromeStatus());
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"path":""}""")]
    [InlineData("""{"path":"C:\\x\u0000y"}""")]
    [InlineData("""{"path":"\ud800"}""")]
    [InlineData("""{"path":42}""")]
    [InlineData("""[1]""")]
    public void Odd_Manifests_Lead_Elsewhere_Without_Throwing(string text)
    {
        PlantManifest(text);
        Assert.Equal(RegistrationState.Elsewhere, ChromeStatus());
    }

    [Fact]
    public void Oversized_Manifests_Lead_Elsewhere_Without_Being_Read_Whole()
    {
        PlantManifest("{\"path\":\"" + new string('a', 70 * 1024) + "\"}");
        Assert.Equal(RegistrationState.Elsewhere, ChromeStatus());
        Assert.Null(BrowserRegistration.Check(CompanionBrowser.Chrome, _exe, _paths, _registry).Target);
    }

    [Fact]
    public void A_Foreign_Manifest_Names_Its_Program()
    {
        string evil = Path.Combine(_root, "evil", "host");
        PlantManifest(BrowserRegistration.ManifestJson(CompanionBrowser.Chrome, evil));
        Assert.Equal(new RegistrationCheck(RegistrationState.Elsewhere, evil), BrowserRegistration.Check(CompanionBrowser.Chrome, _exe, _paths, _registry));
    }

    [Fact]
    public void On_Windows_The_Key_Must_Name_A_Local_Manifest_In_Text()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        BrowserRegistration.Register(CompanionBrowser.Chrome, _exe, _paths, _registry);
        string key = BrowserRegistration.RegistryKey(CompanionBrowser.Chrome);
        foreach (string named in new[] { "dev.dboudreau.xaultwallet.chrome.json", @"\\server\share\m.json", @"%LOCALAPPDATA%\m.json" })
        {
            _registry.SetValue(key, string.Empty, named);
            Assert.Equal(RegistrationState.Elsewhere, ChromeStatus());
        }

        _registry.NotText.Add(key); // e.g. a REG_BINARY default value
        Assert.Equal(RegistrationState.Elsewhere, ChromeStatus());
    }

    [Fact]
    public async Task On_Unix_A_Linked_Manifest_Or_A_Pipe_Is_Not_This_Apps()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string manifest = PlantManifest(BrowserRegistration.ManifestJson(CompanionBrowser.Chrome, _exe));
        string real = Path.Combine(_root, "elsewhere.json");
        File.Move(manifest, real);
        File.CreateSymbolicLink(manifest, real);
        Assert.Equal(RegistrationState.Elsewhere, ChromeStatus());

        // A pipe would block a reader forever: it is never opened.
        File.Delete(manifest);
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [manifest]))
        {
            mkfifo.WaitForExit();
        }

        RegistrationState state = await Task.Run(ChromeStatus).WaitAsync(TimeSpan.FromSeconds(10)); // throws if it hangs
        Assert.NotEqual(RegistrationState.Registered, state);
    }

    [Fact]
    public void Monero_Links_On_Windows_Use_A_Per_User_Protocol()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(RegistrationState.NotRegistered, MoneroLinkRegistration.Status(_exe, _paths, _registry));
        MoneroLinkRegistration.Register(_exe, _paths, _registry);
        Assert.Equal(RegistrationState.Registered, MoneroLinkRegistration.Status(_exe, _paths, _registry));
        Assert.Equal(string.Empty, _registry.GetValue(@"Software\Classes\monero", "URL Protocol"));
        Assert.Equal("\"" + _exe + "\" \"%1\"", _registry.GetValue(@"Software\Classes\monero\shell\open\command", string.Empty));

        // Another wallet taking monero: links over is "elsewhere", and unregistering leaves it alone.
        _registry.SetValue(@"Software\Classes\monero\shell\open\command", string.Empty, "\"C:\\Other\\wallet.exe\" \"%1\"");
        Assert.Equal(RegistrationState.Elsewhere, MoneroLinkRegistration.Status(_exe, _paths, _registry));
        MoneroLinkRegistration.Unregister(_exe, _paths, _registry);
        Assert.NotNull(_registry.GetValue(@"Software\Classes\monero\shell\open\command", string.Empty));

        MoneroLinkRegistration.Register(_exe, _paths, _registry);
        MoneroLinkRegistration.Unregister(_exe, _paths, _registry);
        Assert.Empty(_registry.Keys);

        Assert.False(MoneroLinkRegistration.WindowsUserChoiceOverrides(_registry));
        _registry.SetValue(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\monero\UserChoice", "ProgId", "FeatherWallet.monero");
        Assert.True(MoneroLinkRegistration.WindowsUserChoiceOverrides(_registry));

        // Another way to run something else for the same links (a COM handler, another default verb) is noticed.
        MoneroLinkRegistration.Register(_exe, _paths, _registry);
        _registry.SetValue(@"Software\Classes\monero\shell\open\command", "DelegateExecute", "{00000000-0000-0000-0000-000000000001}");
        Assert.Equal(RegistrationState.Elsewhere, MoneroLinkRegistration.Status(_exe, _paths, _registry));
        MoneroLinkRegistration.Register(_exe, _paths, _registry);
        _registry.SetValue(@"Software\Classes\monero\shell", string.Empty, "pwn");
        Assert.Equal(RegistrationState.Elsewhere, MoneroLinkRegistration.Status(_exe, _paths, _registry));
        MoneroLinkRegistration.Register(_exe, _paths, _registry);
        Assert.Equal(RegistrationState.Registered, MoneroLinkRegistration.Status(_exe, _paths, _registry));

        // What a link command can't hold is refused; what can't be read counts as another program's.
        Assert.Throws<ArgumentException>(() => MoneroLinkRegistration.Register(Path.Combine(_root, "100%", "XaultWallet.exe"), _paths, _registry));
        _registry.NotText.Add(@"Software\Classes\monero\shell\open\command");
        Assert.Equal(RegistrationState.Elsewhere, MoneroLinkRegistration.Status(_exe, _paths, _registry));
        Assert.Null(MoneroLinkRegistration.CurrentHandler(_paths, _registry));
    }

    [Fact]
    public void Monero_Links_On_Linux_Belong_To_The_Copy_That_Registered_Them()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string other = Path.Combine(_root, "other", "XaultWallet");
        MoneroLinkRegistration.Register(other, _paths, null);
        Assert.Equal(RegistrationState.Elsewhere, MoneroLinkRegistration.Status(_exe, _paths, null));

        // This copy's uninstall leaves the other copy's link handler as it was.
        MoneroLinkRegistration.Unregister(_exe, _paths, null);
        Assert.Equal(RegistrationState.Registered, MoneroLinkRegistration.Status(other, _paths, null));

        // An Exec line anywhere but the [Desktop Entry] group doesn't count.
        string desktop = Path.Combine(_paths.DataHome, "applications", "xaultwallet-monero-link.desktop");
        File.WriteAllText(desktop, "[Desktop Entry]\nExec=/tmp/evil %u\n[Desktop Action x]\nExec=\"" + _exe + "\" %u\n");
        Assert.Equal(RegistrationState.Elsewhere, MoneroLinkRegistration.Status(_exe, _paths, null));
    }

    [Fact]
    public void Monero_Links_On_Linux_Read_Spaced_Keys_And_Write_One_Line()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string list = Path.Combine(_paths.ConfigHome, "mimeapps.list");
        Directory.CreateDirectory(_paths.ConfigHome);
        File.WriteAllText(list, "[Default Applications]\nx-scheme-handler/monero = feather.desktop;\n");
        Assert.Equal("feather.desktop", MoneroLinkRegistration.CurrentHandler(_paths, null));

        MoneroLinkRegistration.Register(_exe, _paths, null);
        Assert.Equal("[Default Applications]\nx-scheme-handler/monero=xaultwallet-monero-link.desktop;\n", File.ReadAllText(list));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_paths.DataHome, "applications", "xaultwallet-monero-link.desktop")));

        // A mimeapps.list kept as a link (dotfiles) is written through and stays a link.
        string real = Path.Combine(_root, "dotfiles-mimeapps.list");
        File.Move(list, real);
        File.CreateSymbolicLink(list, real);
        MoneroLinkRegistration.Unregister(_exe, _paths, null);
        Assert.NotNull(new FileInfo(list).LinkTarget);
        Assert.Equal("[Default Applications]\n", File.ReadAllText(real));
    }

    [Fact]
    public void Monero_Links_On_Linux_Use_A_Desktop_Entry_And_Keep_Mimeapps_Intact()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string list = Path.Combine(_paths.ConfigHome, "mimeapps.list");
        Directory.CreateDirectory(_paths.ConfigHome);
        File.WriteAllText(list, "[Default Applications]\ntext/html=firefox.desktop\nx-scheme-handler/monero=feather.desktop;\n\n[Added Associations]\nimage/png=eog.desktop;\n");

        Assert.Equal(RegistrationState.Elsewhere, MoneroLinkRegistration.Status(_exe, _paths, null));
        MoneroLinkRegistration.Register(_exe, _paths, null);
        Assert.Equal(RegistrationState.Registered, MoneroLinkRegistration.Status(_exe, _paths, null));

        string text = File.ReadAllText(list);
        Assert.Contains("text/html=firefox.desktop\n", text, StringComparison.Ordinal);
        Assert.Contains("x-scheme-handler/monero=xaultwallet-monero-link.desktop;\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("feather", text, StringComparison.Ordinal);
        Assert.Contains("[Added Associations]\nimage/png=eog.desktop;", text, StringComparison.Ordinal);

        string entry = File.ReadAllText(Path.Combine(_paths.DataHome, "applications", "xaultwallet-monero-link.desktop"));
        Assert.Contains("\nExec=\"" + _exe + "\" %u\n", entry, StringComparison.Ordinal);
        Assert.Contains("\nMimeType=x-scheme-handler/monero;\n", entry, StringComparison.Ordinal);

        MoneroLinkRegistration.Unregister(_exe, _paths, null);
        Assert.Equal(RegistrationState.NotRegistered, MoneroLinkRegistration.Status(_exe, _paths, null));
        Assert.Equal("[Default Applications]\ntext/html=firefox.desktop\n\n[Added Associations]\nimage/png=eog.desktop;\n", File.ReadAllText(list));
    }

    [Fact]
    public void Monero_Links_On_Linux_Start_A_Missing_Mimeapps_File()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        MoneroLinkRegistration.Register(_exe, _paths, null);
        Assert.Equal("[Default Applications]\nx-scheme-handler/monero=xaultwallet-monero-link.desktop;\n", File.ReadAllText(Path.Combine(_paths.ConfigHome, "mimeapps.list")));
    }

    [Theory]
    [InlineData("/opt/x\"y/XaultWallet")]
    [InlineData("/opt/$HOME/XaultWallet")]
    [InlineData("/opt/100%/XaultWallet")]
    [InlineData("/opt/a`b/XaultWallet")]
    public void Paths_A_Desktop_Entry_Cannot_Quote_Are_Refused(string path) =>
        Assert.Throws<ArgumentException>(() => MoneroLinkRegistration.DesktopExec(path));
}
