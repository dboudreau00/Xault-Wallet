using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using XaultWallet.Core.Installer;
using XaultWallet.Core.Tests.Installer;
using XaultWallet.Core.Tor;
using Xunit;

namespace XaultWallet.Core.Tests.Tor;

/// <summary>
/// The Tor installer end to end against a fake torproject.org: an update channel naming a
/// version, a checksum list signed (detached) by a test key the installer is told to trust, and a
/// real .tar.gz in the Expert Bundle's layout, with the parts tor doesn't need and entries that try
/// to escape the install folder. Every failure must leave nothing installed.
/// </summary>
public sealed class TorInstallerTests : IDisposable
{
    private const string Version = "15.0.30";
    private const string Platform = "linux-x86_64";
    private const string Channel = "https://updates.example.test/release/";
    private const string Dist = "https://dist.example.test/torbrowser/";

    private static readonly byte[] TorBytes = Encoding.ASCII.GetBytes("#!/bin/sh\necho 'Tor version 0.4.9.99.'\n");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xw-tor-installer-test-" + Guid.NewGuid().ToString("N"));
    private readonly TestPgp _signer = new();

    public void Dispose()
    {
        _signer.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static string Tor => TorInstaller.TorFileName;

    [Fact]
    public async Task Installs_Only_What_Tor_Needs_And_Records_Where_It_Came_From()
    {
        byte[] archive = Bundle();
        FakeServer server = Serve(archive);
        var stages = new List<InstallStage>();

        InstalledTor installed = await Installer(server).InstallAsync(new SyncProgress(p => stages.Add(p.Stage)), default);

        string dir = Path.Combine(_root, "v" + Version);
        Assert.Equal(Path.Combine(dir, Tor), installed.Path);
        Assert.Equal(Version, installed.Version);
        Assert.Equal(TorBytes, File.ReadAllBytes(installed.Path));
        Assert.Equal(
            new[] { "geoip", "geoip6", "install.json", "libevent-2.1.so.7", Tor }.OrderBy(n => n, StringComparer.Ordinal),
            Directory.EnumerateFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(_root, "escaped")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped")));
        string manifest = File.ReadAllText(Path.Combine(dir, "install.json"));
        Assert.Contains(Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), manifest);
        Assert.Contains(TorSigningKeys.TorBrowserSigningSubkey, manifest);
        Assert.Equal(
            new[]
            {
                InstallStage.FetchingList, InstallStage.CheckingSignature, InstallStage.Downloading, InstallStage.CheckingChecksum,
                InstallStage.Extracting, InstallStage.Testing, InstallStage.Done,
            },
            stages.Distinct());
        Assert.Equal(installed.Path, TorInstaller.FindInstalled(_root));
        AssertNoStagingLeft();
    }

    [Fact]
    public async Task Archive_Not_Matching_The_Signed_Checksum_Is_Thrown_Away()
    {
        FakeServer server = Serve(Bundle(), serveInstead: Bundle(torBytes: "malware"u8.ToArray()));

        var ex = await Assert.ThrowsAsync<TorInstallException>(() => Installer(server).InstallAsync(null, default));

        Assert.Contains("doesn't match the checksum Tor Project signed", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task List_Signed_By_Someone_Else_Installs_Nothing_And_Downloads_Nothing()
    {
        using var stranger = new TestPgp();
        FakeServer server = Serve(Bundle(), listSigner: stranger);

        var ex = await Assert.ThrowsAsync<TorInstallException>(() => Installer(server).InstallAsync(null, default));

        Assert.Contains("no valid signature", ex.Message);
        Assert.Contains("Nothing was installed", ex.Message);
        Assert.DoesNotContain(server.Requests, r => r.EndsWith(".tar.gz", StringComparison.Ordinal));
        AssertNothingInstalled();
    }

    [Fact]
    public async Task Older_Than_Already_Installed_Is_Refused()
    {
        string keep = Directory.CreateDirectory(Path.Combine(_root, "v15.1.2")).FullName;
        File.WriteAllBytes(Path.Combine(keep, Tor), TorBytes);
        FakeServer server = Serve(Bundle());

        var ex = await Assert.ThrowsAsync<TorInstallException>(() => Installer(server).InstallAsync(null, default));

        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(server.Requests, r => r.EndsWith(".tar.gz", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(keep, Tor), TorInstaller.FindInstalled(_root));
    }

    [Fact]
    public async Task Below_The_Built_In_Minimum_Is_Refused()
    {
        FakeServer server = Serve(Bundle(), version: "14.5.9");

        var ex = await Assert.ThrowsAsync<TorInstallException>(() => Installer(server).InstallAsync(null, default));

        Assert.Contains("minimum", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(server.Requests, r => r.Contains("sha256sums", StringComparison.Ordinal));
        AssertNothingInstalled();
    }

    [Fact]
    public async Task A_Version_That_Is_Not_A_Plain_Number_Never_Becomes_A_Url()
    {
        FakeServer server = Serve(Bundle(), version: "15.0.30/../../evil");

        var ex = await Assert.ThrowsAsync<TorInstallException>(() => Installer(server).InstallAsync(null, default));

        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(server.Requests); // the update channel, nothing after it
        AssertNothingInstalled();
    }

    [Fact]
    public async Task Bundle_Without_Tor_Installs_Nothing()
    {
        FakeServer server = Serve(Bundle(includeTor: false));

        var ex = await Assert.ThrowsAsync<TorInstallException>(() => Installer(server).InstallAsync(null, default));

        Assert.Contains("not in the downloaded Tor Expert Bundle", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task Tor_That_Does_Not_Run_Is_Not_Installed()
    {
        FakeServer server = Serve(Bundle());
        TorInstaller installer = Installer(server, probe: (_, _) => throw new InvalidOperationException("exec format error"));

        var ex = await Assert.ThrowsAsync<TorInstallException>(() => installer.InstallAsync(null, default));

        Assert.Contains("didn't run on this system", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task A_Newer_Install_Replaces_Older_Versions()
    {
        string old = Directory.CreateDirectory(Path.Combine(_root, "v15.0.20")).FullName;
        File.WriteAllBytes(Path.Combine(old, Tor), TorBytes);

        InstalledTor installed = await Installer(Serve(Bundle())).InstallAsync(null, default);

        Assert.False(Directory.Exists(old));
        Assert.Equal(installed.Path, TorInstaller.FindInstalled(_root));
    }

    [Fact]
    public void Find_Installed_Picks_The_Newest_Version_That_Has_Tor()
    {
        foreach (string v in new[] { "15.0.9", "15.0.24", "15.0.10" })
        {
            string dir = Directory.CreateDirectory(Path.Combine(_root, "v" + v)).FullName;
            File.WriteAllBytes(Path.Combine(dir, Tor), TorBytes);
        }

        Directory.CreateDirectory(Path.Combine(_root, "v16.0")); // empty: an interrupted install
        Assert.Equal(Path.Combine(_root, "v15.0.24", Tor), TorInstaller.FindInstalled(_root));
        Assert.Null(TorInstaller.FindInstalled(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Install_Folder_Must_Be_A_Full_Path() =>
        Assert.Throws<ArgumentException>(() => new TorInstaller("relative/folder", null));

    // ---- helpers ----

    private TorInstaller Installer(FakeServer server, Func<string, CancellationToken, Task<string>>? probe = null) =>
        new(_root, proxyAddress: null)
        {
            UpdateChannel = new Uri(Channel),
            Distribution = new Uri(Dist),
            TrustedKeysForTesting = [_signer.Key],
            HandlerForTesting = () => server,
            PlatformForTesting = Platform,
            ProbeBinary = probe ?? ((_, _) => Task.FromResult("Tor version 0.4.9.99.")),
        };

    /// <summary>A fake torproject.org: the update channel names <paramref name="version"/>, the release
    /// folder holds a signed checksum list naming <paramref name="archive"/>, and the archive itself
    /// (or another one).</summary>
    private FakeServer Serve(byte[] archive, byte[]? serveInstead = null, TestPgp? listSigner = null, string version = Version)
    {
        string bundle = TorReleaseList.BundleFileName(Platform, version);
        string sums =
            new string('1', 64) + "  tor-browser-linux-x86_64-" + version + ".tar.xz\n" +
            Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant() + "  " + bundle + "\n" +
            new string('2', 64) + "  tor-expert-bundle-windows-x86_64-" + version + ".tar.gz\n";
        byte[] sumsBytes = Encoding.UTF8.GetBytes(sums);

        var server = new FakeServer();
        server.Routes[Channel + TorReleaseList.UpdateJsonName(Platform)] = Encoding.UTF8.GetBytes($$"""{"version":"{{version}}","binary":"ignored"}""");
        server.Routes[$"{Dist}{version}/sha256sums-signed-build.txt"] = sumsBytes;
        server.Routes[$"{Dist}{version}/sha256sums-signed-build.txt.asc"] = Encoding.ASCII.GetBytes((listSigner ?? _signer).DetachedSign(sumsBytes));
        server.Routes[$"{Dist}{version}/{bundle}"] = serveInstead ?? archive;
        return server;
    }

    /// <summary>A .tar.gz laid out like the Expert Bundle, with extras the installer must leave alone.</summary>
    private static byte[] Bundle(bool includeTor = true, byte[]? torBytes = null)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Gnu, leaveOpen: true))
        {
            void File(string name, byte[] content) =>
                writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });

            writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "data/"));
            File("data/geoip", "geoip v4"u8.ToArray());
            File("data/geoip6", "geoip v6"u8.ToArray());
            File("data/torrc-defaults", "ClientOnly 1"u8.ToArray());
            File("docs/tor.txt", "licence"u8.ToArray());
            writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "tor/"));
            if (includeTor)
            {
                File("tor/" + Tor, torBytes ?? TorBytes);
            }

            File("tor/libevent-2.1.so.7", "lib"u8.ToArray());
            File("tor/pluggable_transports/lyrebird", "pt"u8.ToArray());
            File("tor/../escaped", "out of the folder"u8.ToArray());
            File("../escaped", "out of the folder"u8.ToArray());
            File("debug/tor", "debug build"u8.ToArray());
        }

        using var gz = new MemoryStream();
        using (var compressor = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(tar.ToArray());
        }

        return gz.ToArray();
    }

    private void AssertNothingInstalled()
    {
        Assert.Null(TorInstaller.FindInstalled(_root));
        Assert.False(Directory.Exists(_root) && Directory.EnumerateDirectories(_root, "v*").Any());
        AssertNoStagingLeft();
    }

    private void AssertNoStagingLeft() =>
        Assert.False(Directory.Exists(_root) && Directory.EnumerateDirectories(_root, ".staging-*").Any());

    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Routes { get; } = new();

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(url);
            }

            return Task.FromResult(Routes.TryGetValue(url, out byte[]? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });
        }
    }

    private sealed class SyncProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}
