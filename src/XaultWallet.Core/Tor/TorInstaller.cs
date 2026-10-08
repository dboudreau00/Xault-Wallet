using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XaultWallet.Core.Installer;
using XaultWallet.Core.Security;

namespace XaultWallet.Core.Tor;

/// <summary>A Tor that <see cref="TorInstaller"/> put in place.</summary>
/// <param name="Path">Full path to the tor binary.</param>
/// <param name="Version">The Tor Browser release it came from, e.g. "15.0.24".</param>
/// <param name="VersionLine">What the binary itself printed for --version.</param>
public sealed record InstalledTor(string Path, string Version, string VersionLine);

/// <summary>An install that didn't happen. The message says why and is safe to show; nothing was
/// installed.</summary>
public sealed class TorInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// "Download and install Tor for me", the way the wallet-rpc installer works and the way a careful
/// user would do it by hand:
/// <list type="number">
/// <item>ask Tor Project's update channel which release is current (unsigned: it only picks a folder);</item>
/// <item>fetch that release's sha256sums-signed-build.txt and its detached signature, and check the
/// signature against the Tor Browser Developers key that ships with the app
/// (<see cref="TorSigningKeys"/>), never a key fetched now;</item>
/// <item>download this system's Tor Expert Bundle and require its SHA-256 to equal the SIGNED one;</item>
/// <item>unpack only tor (plus its libraries and GeoIP files), run it once (--version), and only then
/// move it into <c>&lt;root&gt;/v&lt;version&gt;/</c>.</item>
/// </list>
/// A release older than <see cref="MinimumVersion"/>, or older than one already installed, is refused,
/// so a rolled-back channel can't push an old Tor. Any failure leaves nothing installed. Traffic goes
/// direct, or through the user's own SOCKS proxy when one is given: the managed Tor can't download
/// itself.
/// </summary>
public sealed class TorInstaller
{
    public static readonly Uri OfficialUpdateChannel = new("https://aus1.torproject.org/torbrowser/update_3/release/");
    public static readonly Uri OfficialDistribution = new("https://dist.torproject.org/torbrowser/");

    /// <summary>The oldest Tor Browser release whose Expert Bundle this app installs.</summary>
    public static readonly Version MinimumVersion = new(15, 0);

    private const long MaxSmallFileBytes = 256 * 1024;
    private const string SumsName = "sha256sums-signed-build.txt";
    private const string ManifestName = "install.json";
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    private readonly string _root;
    private readonly string? _proxy;

    /// <param name="installRoot">The folder installs go under (one subfolder per version).</param>
    /// <param name="proxyAddress">SOCKS proxy "host:port" to download through, or null/empty for direct.</param>
    public TorInstaller(string installRoot, string? proxyAddress)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !System.IO.Path.IsPathFullyQualified(installRoot))
        {
            throw new ArgumentException("The install folder must be a full path.", nameof(installRoot));
        }

        _root = installRoot;
        _proxy = string.IsNullOrWhiteSpace(proxyAddress) ? null : proxyAddress.Trim();
    }

    // ---- Test seams (internal): the app always uses torproject.org and the pinned key. ----
    internal Uri UpdateChannel { get; init; } = OfficialUpdateChannel;
    internal Uri Distribution { get; init; } = OfficialDistribution;
    internal IReadOnlyList<PgpRsaKey>? TrustedKeysForTesting { get; init; }
    internal Func<HttpMessageHandler>? HandlerForTesting { get; init; }
    internal string? PlatformForTesting { get; init; }

    /// <summary>Stand-in for "run it once": returns what the binary prints for --version.</summary>
    internal Func<string, CancellationToken, Task<string>> ProbeBinary { get; init; } =
        (path, ct) => TorDiagnostics.ProbeTorAsync(path, ct);

    /// <summary>The tor binary's file name on this system.</summary>
    public static string TorFileName => OperatingSystem.IsWindows() ? "tor.exe" : "tor";

    /// <summary>Download, verify and install. See the class summary for the steps.</summary>
    /// <exception cref="TorInstallException">Anything that stopped the install (message is user-facing).</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing was installed.</exception>
    public async Task<InstalledTor> InstallAsync(IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        string platform = PlatformForTesting ?? TorReleaseList.CurrentPlatform()
            ?? throw new TorInstallException("Tor Project publishes no Tor Expert Bundle for this system. Install tor yourself and choose it in Settings.");

        using HttpClient http = CreateClient();

        progress?.Report(new InstallProgress(InstallStage.FetchingList));
        string version;
        try
        {
            string json = await FetchTextAsync(http, new Uri(UpdateChannel, TorReleaseList.UpdateJsonName(platform)), ct).ConfigureAwait(false);
            version = TorReleaseList.ParseCurrentVersion(json);
        }
        catch (InvalidOperationException ex)
        {
            throw new TorInstallException(ex.Message + " Nothing was installed.", ex);
        }

        RefuseIfBelowMinimum(version);
        RefuseIfOlderThanInstalled(version);

        Uri releaseDir = new(Distribution, version + "/");
        byte[] sums = await FetchBytesAsync(http, new Uri(releaseDir, SumsName), ct).ConfigureAwait(false);
        string signature = Encoding.ASCII.GetString(await FetchBytesAsync(http, new Uri(releaseDir, SumsName + ".asc"), ct).ConfigureAwait(false));

        progress?.Report(new InstallProgress(InstallStage.CheckingSignature));
        TorBundleArchive archive;
        try
        {
            OpenPgp.VerifyDetached(sums, signature, TrustedKeysForTesting ?? TorSigningKeys.Trusted,
                "Tor Project's checksum list", TorSigningKeys.SignerName);
            archive = TorReleaseList.Select(Encoding.UTF8.GetString(sums).Split('\n'), platform, version);
        }
        catch (Exception ex) when (ex is SignatureCheckException or InvalidOperationException)
        {
            throw new TorInstallException(ex.Message + " Nothing was installed.", ex);
        }

        string staging = System.IO.Path.Combine(_root, ".staging-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        try
        {
            PrivateFiles.EnsureDirectory(_root);
            PrivateFiles.EnsureDirectory(staging);

            string archivePath = System.IO.Path.Combine(staging, archive.FileName);
            byte[] sha256;
            try
            {
                sha256 = await WalletRpcInstaller.DownloadAsync(http, new Uri(releaseDir, archive.FileName), archivePath, progress, ct).ConfigureAwait(false);
            }
            catch (WalletRpcInstallException ex)
            {
                throw new TorInstallException(ex.Message, ex);
            }

            progress?.Report(new InstallProgress(InstallStage.CheckingChecksum));
            if (!CryptographicOperations.FixedTimeEquals(sha256, Convert.FromHexString(archive.Sha256)))
            {
                throw new TorInstallException(
                    "The download doesn't match the checksum Tor Project signed, so it was thrown away and nothing was installed. " +
                    "Try again later, or install tor yourself.");
            }

            progress?.Report(new InstallProgress(InstallStage.Extracting));
            string unpacked = PrivateFiles.EnsureDirectory(System.IO.Path.Combine(staging, "tor"));
            try
            {
                ArchiveExtractor.ExtractTorBundle(archivePath, TorFileName, unpacked, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
            {
                throw new TorInstallException($"Couldn't unpack Tor from the download: {ex.Message}", ex);
            }

            string stagedBinary = System.IO.Path.Combine(unpacked, TorFileName);
            if (!OperatingSystem.IsWindows())
            {
                foreach (string f in Directory.EnumerateFiles(unpacked))
                {
                    File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            progress?.Report(new InstallProgress(InstallStage.Testing));
            string versionLine;
            try
            {
                versionLine = await ProbeBinary(stagedBinary, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TorInstallException("The downloaded tor didn't run on this system: " + ex.Message, ex);
            }

            string installed = MoveIntoPlace(unpacked, archive, sha256);
            RemoveOtherVersions(archive.Version);

            progress?.Report(new InstallProgress(InstallStage.Done));
            return new InstalledTor(installed, archive.Version, versionLine);
        }
        catch (IOException ex)
        {
            throw new TorInstallException("Couldn't write the install folder: " + ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TorInstallException("Couldn't write the install folder: " + ex.Message, ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort: a later install starts its own staging folder
            }
        }
    }

    /// <summary>The newest tor a previous install left under <paramref name="installRoot"/>, or null.</summary>
    public static string? FindInstalled(string installRoot) =>
        InstalledVersions(installRoot).Select(x => System.IO.Path.Combine(x.dir, TorFileName)).FirstOrDefault();

    /// <summary>Version folders under the root that still hold a tor binary, newest first.</summary>
    private static List<(string dir, Version version)> InstalledVersions(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return [];
            }

            return Directory.EnumerateDirectories(root, "v*")
                .Select(dir => (dir, version: Version.TryParse(System.IO.Path.GetFileName(dir)[1..], out Version? v) ? v : null))
                .Where(x => x.version is not null && File.Exists(System.IO.Path.Combine(x.dir, TorFileName)))
                .Select(x => (x.dir, x.version!))
                .OrderByDescending(x => x.Item2)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void RefuseIfBelowMinimum(string version)
    {
        if (!Version.TryParse(version, out Version? candidate) || candidate < MinimumVersion)
        {
            throw new TorInstallException(
                $"Tor Project's update channel offers {version}, which is older than the minimum this app accepts " +
                $"({MinimumVersion}). Nothing was installed.");
        }
    }

    private void RefuseIfOlderThanInstalled(string version)
    {
        Version candidate = Version.Parse(version);
        Version? newest = InstalledVersions(_root).Select(x => x.version).FirstOrDefault();
        if (newest is not null && candidate < newest)
        {
            throw new TorInstallException(
                $"Tor Project's update channel offers {version}, but {newest} is already installed. " +
                "Refusing to replace a newer Tor with an older one. Nothing was installed.");
        }
    }

    private HttpClient CreateClient()
    {
        HttpMessageHandler handler = HandlerForTesting?.Invoke() ?? WalletRpcInstaller.CreateHandler(_proxy);
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XaultWallet");
        return http;
    }

    private async Task<string> FetchTextAsync(HttpClient http, Uri url, CancellationToken ct) =>
        Encoding.UTF8.GetString(await FetchBytesAsync(http, url, ct).ConfigureAwait(false));

    /// <summary>A small file (JSON, checksum list, signature), capped and with a timeout.</summary>
    private async Task<byte[]> FetchBytesAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            try
            {
                WalletRpcInstaller.EnsureOk(resp, url);
            }
            catch (WalletRpcInstallException ex)
            {
                throw new TorInstallException(ex.Message, ex);
            }

            if (resp.Content.Headers.ContentLength > MaxSmallFileBytes)
            {
                throw new TorInstallException($"A file from {url.Host} is unexpectedly large. Nothing was installed.");
            }

            await using Stream body = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16384];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxSmallFileBytes)
                {
                    throw new TorInstallException($"A file from {url.Host} is unexpectedly large. Nothing was installed.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TorInstallException($"{url.Host} didn't answer in time. Check your connection" + ProxyHint() + " and try again.");
        }
        catch (HttpRequestException ex)
        {
            throw new TorInstallException($"Couldn't reach {url.Host}: " + ex.Message + ProxyHint(), ex);
        }
    }

    private string ProxyHint() => _proxy is null ? string.Empty : $" (it goes through your proxy {_proxy})";

    /// <summary>Move the tested files to &lt;root&gt;/v&lt;version&gt;/ and record what they came from.</summary>
    private string MoveIntoPlace(string unpacked, TorBundleArchive archive, byte[] archiveSha256)
    {
        string versionDir = System.IO.Path.Combine(_root, "v" + archive.Version);
        string target = System.IO.Path.Combine(versionDir, TorFileName);
        if (Directory.Exists(versionDir) && File.Exists(target)
            && CryptographicOperations.FixedTimeEquals(HashFile(target), HashFile(System.IO.Path.Combine(unpacked, TorFileName))))
        {
            // Same release already installed, byte for byte (it may be running: leave it alone).
        }
        else
        {
            try
            {
                if (Directory.Exists(versionDir))
                {
                    Directory.Delete(versionDir, recursive: true);
                }

                Directory.Move(unpacked, versionDir);
            }
            catch (IOException ex)
            {
                throw new TorInstallException($"Tor {archive.Version} is already installed and running. Stop Tor in Settings and try again.", ex);
            }
        }

        var manifest = new Dictionary<string, string>
        {
            ["version"] = archive.Version,
            ["archive"] = archive.FileName,
            ["archiveSha256"] = Convert.ToHexString(archiveSha256).ToLowerInvariant(),
            ["torSha256"] = Convert.ToHexString(HashFile(target)).ToLowerInvariant(),
            ["signedChecksums"] = new Uri(new Uri(Distribution, archive.Version + "/"), SumsName).ToString(),
            ["signingKey"] = TorSigningKeys.TorBrowserSigningSubkey,
            ["installedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        PrivateFiles.WriteAllText(System.IO.Path.Combine(versionDir, ManifestName), JsonSerializer.Serialize(manifest, ManifestJson));
        return target;
    }

    /// <summary>Older installs are removed once a newer one works; one in use is left for next time.</summary>
    private void RemoveOtherVersions(string keepVersion)
    {
        foreach (string dir in Directory.EnumerateDirectories(_root, "v*"))
        {
            if (string.Equals(System.IO.Path.GetFileName(dir), "v" + keepVersion, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use; next install retries
            }
        }
    }

    private static byte[] HashFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return SHA256.HashData(fs);
    }
}
