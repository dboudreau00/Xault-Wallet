using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;
using XaultWallet.Core.Installer;
using Xunit;

namespace XaultWallet.Core.Tests.Installer;

/// <summary>
/// The installer end to end against a fake getmonero.org: a hash list signed by a test key the
/// installer is told to trust, and real .zip / .tar.bz2 archives built here in the same layout as
/// Monero's. Every failure must leave nothing installed and no download behind.
/// </summary>
public sealed class WalletRpcInstallerTests : IDisposable
{
    private const string Version = "0.18.9.9";
    private static readonly byte[] RpcBytes = Encoding.ASCII.GetBytes("#!/bin/sh\necho 'Monero wallet-rpc test build'\n");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xw-installer-test-" + Guid.NewGuid().ToString("N"));
    private readonly TestPgp _signer = new();

    public void Dispose()
    {
        _signer.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task Installs_From_A_Tar_Bz2_Archive_And_Records_What_It_Installed()
    {
        byte[] archive = TarBz2($"monero-x86_64-linux-gnu-v{Version}/monero-wallet-rpc", RpcBytes);
        var server = Serve("linux-x64", $"monero-linux-x64-v{Version}.tar.bz2", archive);

        var stages = new List<InstallStage>();
        InstalledWalletRpc installed = await Installer(server, "linux-x64").InstallAsync(new SyncProgress(p => stages.Add(p.Stage)), default);

        Assert.Equal(Path.Combine(_root, "v" + Version, "monero-wallet-rpc"), installed.Path);
        Assert.Equal(Version, installed.Version);
        Assert.Equal(RpcBytes, File.ReadAllBytes(installed.Path));
        string manifest = File.ReadAllText(Path.Combine(_root, "v" + Version, "install.json"));
        Assert.Contains(Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), manifest);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(RpcBytes)).ToLowerInvariant(), manifest);
        Assert.Equal(
            new[]
            {
                InstallStage.FetchingList, InstallStage.CheckingSignature, InstallStage.Downloading, InstallStage.CheckingChecksum,
                InstallStage.Extracting, InstallStage.Testing, InstallStage.Done,
            },
            stages.Distinct());
        AssertNoStagingLeft();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(installed.Path, WalletRpcInstaller.FindInstalled(_root));
            Assert.True(File.GetUnixFileMode(installed.Path).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task Installs_The_Exe_From_A_Windows_Zip()
    {
        byte[] archive = Zip($"monero-x86_64-w64-mingw32-v{Version}/monero-wallet-rpc.exe", RpcBytes);
        var server = Serve("win-x64", $"monero-win-x64-v{Version}.zip", archive);

        InstalledWalletRpc installed = await Installer(server, "win-x64").InstallAsync(null, default);

        Assert.Equal(Path.Combine(_root, "v" + Version, "monero-wallet-rpc.exe"), installed.Path);
        Assert.Equal(RpcBytes, File.ReadAllBytes(installed.Path));
        AssertNoStagingLeft();
    }

    [Fact]
    public async Task Archive_Not_Matching_The_Signed_Checksum_Is_Thrown_Away()
    {
        byte[] genuine = TarBz2($"monero-x86_64-linux-gnu-v{Version}/monero-wallet-rpc", RpcBytes);
        byte[] swapped = TarBz2($"monero-x86_64-linux-gnu-v{Version}/monero-wallet-rpc", Encoding.ASCII.GetBytes("malware"));
        var server = Serve("linux-x64", $"monero-linux-x64-v{Version}.tar.bz2", genuine, serveInstead: swapped);

        var ex = await Assert.ThrowsAsync<WalletRpcInstallException>(() => Installer(server, "linux-x64").InstallAsync(null, default));

        Assert.Contains("doesn't match the checksum", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task List_Signed_By_Someone_Else_Installs_Nothing_And_Downloads_Nothing()
    {
        using var attacker = new TestPgp();
        byte[] archive = TarBz2($"monero-x86_64-linux-gnu-v{Version}/monero-wallet-rpc", RpcBytes);
        var server = Serve("linux-x64", $"monero-linux-x64-v{Version}.tar.bz2", archive, listSigner: attacker);

        var ex = await Assert.ThrowsAsync<WalletRpcInstallException>(() => Installer(server, "linux-x64").InstallAsync(null, default));

        Assert.Contains("Nothing was installed", ex.Message);
        Assert.DoesNotContain(server.Requests, r => r.EndsWith(".tar.bz2", StringComparison.Ordinal));
        AssertNothingInstalled();
    }

    [Fact]
    public async Task List_Without_A_Build_For_This_System_Is_Reported()
    {
        byte[] archive = Zip($"x/monero-wallet-rpc.exe", RpcBytes);
        var server = Serve("win-x64", $"monero-win-x64-v{Version}.zip", archive);

        var ex = await Assert.ThrowsAsync<WalletRpcInstallException>(() => Installer(server, "mac-armv8").InstallAsync(null, default));

        Assert.Contains("no command-line build for this system", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task Archive_Without_Wallet_Rpc_Installs_Nothing()
    {
        byte[] archive = TarBz2($"monero-x86_64-linux-gnu-v{Version}/monerod", RpcBytes);
        var server = Serve("linux-x64", $"monero-linux-x64-v{Version}.tar.bz2", archive);

        var ex = await Assert.ThrowsAsync<WalletRpcInstallException>(() => Installer(server, "linux-x64").InstallAsync(null, default));

        Assert.Contains("not in the downloaded archive", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task Binary_That_Does_Not_Run_Is_Not_Installed()
    {
        byte[] archive = TarBz2($"monero-x86_64-linux-gnu-v{Version}/monero-wallet-rpc", RpcBytes);
        var server = Serve("linux-x64", $"monero-linux-x64-v{Version}.tar.bz2", archive);
        WalletRpcInstaller installer = Installer(server, "linux-x64", probe: (_, _) => throw new InvalidOperationException("exec format error"));

        var ex = await Assert.ThrowsAsync<WalletRpcInstallException>(() => installer.InstallAsync(null, default));

        Assert.Contains("didn't run on this system", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task Server_Error_Is_Reported_Plainly()
    {
        var server = new FakeServer();
        var ex = await Assert.ThrowsAsync<WalletRpcInstallException>(() => Installer(server, "linux-x64").InstallAsync(null, default));

        Assert.Contains("HTTP 404", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public async Task Cancel_Leaves_Nothing_Behind()
    {
        byte[] archive = TarBz2($"monero-x86_64-linux-gnu-v{Version}/monero-wallet-rpc", RpcBytes);
        var server = Serve("linux-x64", $"monero-linux-x64-v{Version}.tar.bz2", archive);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Stage == InstallStage.CheckingChecksum)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer(server, "linux-x64").InstallAsync(progress, cts.Token));
        AssertNothingInstalled();
    }

    [Fact]
    public async Task A_Newer_Install_Replaces_Older_Versions()
    {
        string exe = Core.Monero.ExecutableLocator.WalletRpcFileName;
        string old = Directory.CreateDirectory(Path.Combine(_root, "v0.18.3.4")).FullName;
        File.WriteAllBytes(Path.Combine(old, exe), RpcBytes);
        Assert.Equal(Path.Combine(old, exe), WalletRpcInstaller.FindInstalled(_root));

        // This system's own archive format, so FindInstalled looks for the same file name.
        (string platform, string fileName, byte[] archive) = OperatingSystem.IsWindows()
            ? ("win-x64", $"monero-win-x64-v{Version}.zip", Zip($"monero-x86_64-w64-mingw32-v{Version}/{exe}", RpcBytes))
            : ("linux-x64", $"monero-linux-x64-v{Version}.tar.bz2", TarBz2($"monero-x86_64-linux-gnu-v{Version}/{exe}", RpcBytes));
        InstalledWalletRpc installed = await Installer(Serve(platform, fileName, archive), platform).InstallAsync(null, default);

        Assert.False(Directory.Exists(old));
        Assert.Equal(installed.Path, WalletRpcInstaller.FindInstalled(_root));
    }

    [Fact]
    public void Find_Installed_Picks_The_Newest_Version()
    {
        foreach (string v in new[] { "0.18.4.0", "0.18.10.1", "0.18.9.0" })
        {
            string dir = Directory.CreateDirectory(Path.Combine(_root, "v" + v)).FullName;
            File.WriteAllBytes(Path.Combine(dir, Core.Monero.ExecutableLocator.WalletRpcFileName), RpcBytes);
        }

        Directory.CreateDirectory(Path.Combine(_root, "v0.19.0.0")); // empty: an interrupted install
        Assert.Equal(Path.Combine(_root, "v0.18.10.1", Core.Monero.ExecutableLocator.WalletRpcFileName),
            WalletRpcInstaller.FindInstalled(_root));
        Assert.Null(WalletRpcInstaller.FindInstalled(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Install_Folder_Must_Be_A_Full_Path() =>
        Assert.Throws<ArgumentException>(() => new WalletRpcInstaller("relative/folder", null));

    // ---- helpers ----

    private WalletRpcInstaller Installer(FakeServer server, string platform, Func<string, CancellationToken, Task<string>>? probe = null) =>
        new(_root, proxyAddress: null)
        {
            HashListUrl = new Uri("https://example.test/downloads/hashes.txt"),
            DownloadBase = new Uri("https://dl.example.test/cli/"),
            TrustedKeysForTesting = [_signer.Key],
            HandlerForTesting = () => server,
            PlatformForTesting = platform,
            ProbeBinary = probe ?? ((_, _) => Task.FromResult($"Monero 'Fluorine Fermi' (v{Version}-release)")),
        };

    /// <summary>A server holding a signed list that names <paramref name="fileName"/> with the
    /// checksum of <paramref name="archive"/>, and serving that archive (or another one).</summary>
    private FakeServer Serve(string platform, string fileName, byte[] archive, byte[]? serveInstead = null, TestPgp? listSigner = null)
    {
        _ = platform;
        string sha = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        string list = (listSigner ?? _signer).ClearSign(
        [
            "# This GPG-signed message exists to confirm the SHA256 sums of Monero binaries.",
            "## CLI",
            "1111111111111111111111111111111111111111111111111111111111111111  monero-android-armv8-v" + Version + ".tar.bz2",
            sha + "  " + fileName,
            "## GUI",
            "2222222222222222222222222222222222222222222222222222222222222222  monero-gui-win-x64-v" + Version + ".zip",
        ]);

        var server = new FakeServer();
        server.Routes["https://example.test/downloads/hashes.txt"] = Encoding.UTF8.GetBytes(list);
        server.Routes["https://dl.example.test/cli/" + fileName] = serveInstead ?? archive;
        return server;
    }

    private void AssertNothingInstalled()
    {
        Assert.Null(WalletRpcInstaller.FindInstalled(_root));
        Assert.False(Directory.Exists(_root) && Directory.EnumerateDirectories(_root, "v*").Any());
        AssertNoStagingLeft();
    }

    private void AssertNoStagingLeft() =>
        Assert.False(Directory.Exists(_root) && Directory.EnumerateDirectories(_root, ".staging-*").Any());

    private static byte[] TarBz2(string entryName, byte[] content)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Gnu, leaveOpen: true))
        {
            writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, entryName[..entryName.LastIndexOf('/')] + "/"));
            writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, entryName[..entryName.LastIndexOf('/')] + "/README.md")
            {
                DataStream = new MemoryStream("readme"u8.ToArray()),
            });
            writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, entryName) { DataStream = new MemoryStream(content) });
        }

        using var bz = new MemoryStream();
        using (var compressor = new BZip2OutputStream(bz) { IsStreamOwner = false })
        {
            compressor.Write(tar.ToArray());
        }

        return bz.ToArray();
    }

    private static byte[] Zip(string entryName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream other = zip.CreateEntry(entryName[..entryName.LastIndexOf('/')] + "/monerod.exe").Open())
            {
                other.Write("monerod"u8);
            }

            using Stream s = zip.CreateEntry(entryName).Open();
            s.Write(content);
        }

        return ms.ToArray();
    }

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

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to a sync context).</summary>
    private sealed class SyncProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}
