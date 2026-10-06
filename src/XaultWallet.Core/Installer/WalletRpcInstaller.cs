using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;

namespace XaultWallet.Core.Installer;

/// <summary>Where an install is.</summary>
public enum InstallStage
{
    FetchingList,
    CheckingSignature,
    Downloading,
    CheckingChecksum,
    Extracting,
    Testing,
    Done,
}

/// <summary>A progress report. <see cref="BytesTotal"/> is 0 when the server didn't say.</summary>
public sealed record InstallProgress(InstallStage Stage, long BytesDone = 0, long BytesTotal = 0);

/// <summary>A monero-wallet-rpc that <see cref="WalletRpcInstaller"/> put in place.</summary>
/// <param name="Path">Full path to the binary.</param>
/// <param name="Version">Monero's version number, e.g. "0.18.5.1".</param>
/// <param name="VersionLine">What the binary itself printed for --version.</param>
public sealed record InstalledWalletRpc(string Path, string Version, string VersionLine);

/// <summary>An install that didn't happen. The message says why and is safe to show; nothing was
/// installed.</summary>
public sealed class WalletRpcInstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The "download and install monero-wallet-rpc for me" button. Same steps a careful user takes by
/// hand, in the same order:
/// <list type="number">
/// <item>fetch getmonero.org's hash list and check its OpenPGP signature against binaryFate's key,
/// which ships with the app (<see cref="MoneroSigningKeys"/>) — never against a key fetched now;</item>
/// <item>download this system's official CLI archive and require its SHA-256 to equal the SIGNED one;</item>
/// <item>copy only monero-wallet-rpc out of it, run it once (--version), and only then move it into
/// <c>&lt;root&gt;/v&lt;version&gt;/</c>.</item>
/// </list>
/// Any failure leaves nothing installed; downloads go to a staging folder that is always deleted.
/// Traffic takes the same route as the wallet's: through the user's SOCKS proxy when one is set
/// (e.g. Tor), otherwise direct (system proxies are ignored, as they are for the wallet).
/// </summary>
public sealed class WalletRpcInstaller
{
    public static readonly Uri OfficialHashListUrl = new("https://www.getmonero.org/downloads/hashes.txt");
    public static readonly Uri OfficialDownloadBase = new("https://downloads.getmonero.org/cli/");

    private const long MaxListBytes = 256 * 1024;
    private const long MaxArchiveBytes = 1024L * 1024 * 1024;
    private const string ManifestName = "install.json";
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    private readonly string _root;
    private readonly string? _proxy;

    /// <param name="installRoot">The folder installs go under (one subfolder per version).</param>
    /// <param name="proxyAddress">SOCKS proxy "host:port" (e.g. Tor), or null/empty for direct.</param>
    public WalletRpcInstaller(string installRoot, string? proxyAddress)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !System.IO.Path.IsPathFullyQualified(installRoot))
        {
            throw new ArgumentException("The install folder must be a full path.", nameof(installRoot));
        }

        _root = installRoot;
        _proxy = string.IsNullOrWhiteSpace(proxyAddress) ? null : proxyAddress.Trim();
    }

    // ---- Test seams (internal): the app always uses getmonero.org and the pinned key. ----
    internal Uri HashListUrl { get; init; } = OfficialHashListUrl;
    internal Uri DownloadBase { get; init; } = OfficialDownloadBase;
    internal IReadOnlyList<PgpRsaKey>? TrustedKeysForTesting { get; init; }
    internal Func<HttpMessageHandler>? HandlerForTesting { get; init; }
    internal string? PlatformForTesting { get; init; }

    /// <summary>Stand-in for "run it once": returns what the binary prints for --version.</summary>
    internal Func<string, CancellationToken, Task<string>> ProbeBinary { get; init; } =
        (path, ct) => MoneroDiagnostics.ProbeWalletRpcAsync(path, ct);

    /// <summary>Download, verify and install. See the class summary for the steps.</summary>
    /// <exception cref="WalletRpcInstallException">Anything that stopped the install (message is user-facing).</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing was installed.</exception>
    public async Task<InstalledWalletRpc> InstallAsync(IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        string platform = PlatformForTesting ?? MoneroReleaseList.CurrentPlatform()
            ?? throw new WalletRpcInstallException("Monero publishes no command-line build for this system. Install monero-wallet-rpc yourself.");

        using HttpClient http = CreateClient();

        progress?.Report(new InstallProgress(InstallStage.FetchingList));
        string list = await FetchListAsync(http, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress(InstallStage.CheckingSignature));
        MoneroCliArchive archive;
        try
        {
            IReadOnlyList<string> signed = OpenPgp.VerifyClearSigned(list, TrustedKeysForTesting ?? MoneroSigningKeys.Trusted);
            archive = MoneroReleaseList.Select(signed, platform);
        }
        catch (Exception ex) when (ex is SignatureCheckException or InvalidOperationException)
        {
            throw new WalletRpcInstallException(ex.Message + " Nothing was installed.", ex);
        }

        string staging = System.IO.Path.Combine(_root, ".staging-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        try
        {
            PrivateFiles.EnsureDirectory(_root);
            PrivateFiles.EnsureDirectory(staging);

            string archivePath = System.IO.Path.Combine(staging, archive.FileName);
            byte[] sha256 = await DownloadAsync(http, new Uri(DownloadBase, archive.FileName), archivePath, progress, ct).ConfigureAwait(false);

            progress?.Report(new InstallProgress(InstallStage.CheckingChecksum));
            if (!CryptographicOperations.FixedTimeEquals(sha256, Convert.FromHexString(archive.Sha256)))
            {
                throw new WalletRpcInstallException(
                    "The download doesn't match the checksum Monero signed, so it was thrown away and nothing was installed. " +
                    "Try again later, or download and verify monero-wallet-rpc yourself.");
            }

            progress?.Report(new InstallProgress(InstallStage.Extracting));
            string fileName = MoneroReleaseList.WalletRpcFileName(archive.Platform);
            string stagedBinary = System.IO.Path.Combine(staging, fileName);
            try
            {
                ArchiveExtractor.ExtractFile(archivePath, fileName, stagedBinary, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException
                                           or ICSharpCode.SharpZipLib.SharpZipBaseException)
            {
                throw new WalletRpcInstallException($"Couldn't unpack {fileName} from the download: {ex.Message}", ex);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stagedBinary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            progress?.Report(new InstallProgress(InstallStage.Testing));
            string versionLine;
            try
            {
                versionLine = await ProbeBinary(stagedBinary, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new WalletRpcInstallException("The downloaded monero-wallet-rpc didn't run on this system: " + ex.Message, ex);
            }

            string installed = MoveIntoPlace(stagedBinary, archive, sha256);
            RemoveOtherVersions(archive.Version);

            progress?.Report(new InstallProgress(InstallStage.Done));
            return new InstalledWalletRpc(installed, archive.Version, versionLine);
        }
        catch (IOException ex)
        {
            throw new WalletRpcInstallException("Couldn't write the install folder: " + ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new WalletRpcInstallException("Couldn't write the install folder: " + ex.Message, ex);
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

    /// <summary>The newest monero-wallet-rpc a previous install left under <paramref name="installRoot"/>,
    /// or null.</summary>
    public static string? FindInstalled(string installRoot)
    {
        try
        {
            if (!Directory.Exists(installRoot))
            {
                return null;
            }

            string fileName = ExecutableLocator.WalletRpcFileName;
            return Directory.EnumerateDirectories(installRoot, "v*")
                .Select(dir => (dir, version: Version.TryParse(System.IO.Path.GetFileName(dir)[1..], out Version? v) ? v : null))
                .Where(x => x.version is not null && File.Exists(System.IO.Path.Combine(x.dir, fileName)))
                .OrderByDescending(x => x.version)
                .Select(x => System.IO.Path.Combine(x.dir, fileName))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private HttpClient CreateClient()
    {
        HttpMessageHandler handler = HandlerForTesting?.Invoke() ?? CreateHandler(_proxy);
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("XaultWallet");
        return http;
    }

    /// <summary>The user's SOCKS proxy when set, otherwise DIRECT — the same route the wallet takes.
    /// Redirects are followed (the download host may hand off to a mirror) but never from HTTPS to
    /// HTTP; the signed checksum makes the mirror irrelevant to integrity anyway.</summary>
    private static SocketsHttpHandler CreateHandler(string? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None,
        };
        if (proxy is not null)
        {
            handler.Proxy = new WebProxy("socks5://" + proxy);
            handler.UseProxy = true;
        }

        return handler;
    }

    private async Task<string> FetchListAsync(HttpClient http, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(HashListUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureOk(resp, HashListUrl);
            if (resp.Content.Headers.ContentLength > MaxListBytes)
            {
                throw new WalletRpcInstallException("The release list from getmonero.org is unexpectedly large. Nothing was installed.");
            }

            await using Stream body = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16384];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxListBytes)
                {
                    throw new WalletRpcInstallException("The release list from getmonero.org is unexpectedly large. Nothing was installed.");
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WalletRpcInstallException("getmonero.org didn't answer in time. Check your connection" + ProxyHint() + " and try again.");
        }
        catch (HttpRequestException ex)
        {
            throw new WalletRpcInstallException("Couldn't reach getmonero.org: " + ex.Message + ProxyHint(), ex);
        }
    }

    /// <summary>Stream the archive to disk, hashing as it arrives. Fails on a stall (60 s without
    /// data) or past <see cref="MaxArchiveBytes"/>. Returns the SHA-256.</summary>
    private static async Task<byte[]> DownloadAsync(HttpClient http, Uri url, string destination, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            EnsureOk(resp, url);
            long total = resp.Content.Headers.ContentLength ?? 0;
            if (total > MaxArchiveBytes)
            {
                throw new WalletRpcInstallException("The download is unexpectedly large, so it was not fetched. Nothing was installed.");
            }

            await using Stream body = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long done = 0;
            long lastReported = -1;
            progress?.Report(new InstallProgress(InstallStage.Downloading, 0, total));
            int read;
            while ((read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(60)); // data arrived: restart the stall clock
                done += read;
                if (done > MaxArchiveBytes)
                {
                    throw new WalletRpcInstallException("The download is unexpectedly large, so it was stopped. Nothing was installed.");
                }

                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                if (done - lastReported >= 256 * 1024)
                {
                    lastReported = done;
                    progress?.Report(new InstallProgress(InstallStage.Downloading, done, total));
                }
            }

            if (total > 0 && done != total)
            {
                throw new WalletRpcInstallException("The download was cut short. Nothing was installed; try again.");
            }

            await file.FlushAsync(stall.Token).ConfigureAwait(false);
            progress?.Report(new InstallProgress(InstallStage.Downloading, done, total));
            return sha.GetHashAndReset();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WalletRpcInstallException("The download stalled. Nothing was installed; check your connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            throw new WalletRpcInstallException("The download failed: " + ex.Message + " Nothing was installed.", ex);
        }
    }

    private static void EnsureOk(HttpResponseMessage resp, Uri url)
    {
        if (!resp.IsSuccessStatusCode)
        {
            throw new WalletRpcInstallException(
                $"{url.Host} answered HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}). Nothing was installed.");
        }
    }

    private string ProxyHint() => _proxy is null ? string.Empty : $" (it goes through your proxy {_proxy})";

    /// <summary>Move the tested binary to &lt;root&gt;/v&lt;version&gt;/ and record what it came from.</summary>
    private string MoveIntoPlace(string stagedBinary, MoneroCliArchive archive, byte[] archiveSha256)
    {
        string versionDir = PrivateFiles.EnsureDirectory(System.IO.Path.Combine(_root, "v" + archive.Version));
        string target = System.IO.Path.Combine(versionDir, System.IO.Path.GetFileName(stagedBinary));
        byte[] binarySha256 = HashFile(stagedBinary);

        if (File.Exists(target) && CryptographicOperations.FixedTimeEquals(HashFile(target), binarySha256))
        {
            // Same version already installed, byte for byte (it may be running: leave it alone).
        }
        else
        {
            try
            {
                File.Move(stagedBinary, target, overwrite: true);
            }
            catch (IOException ex)
            {
                throw new WalletRpcInstallException(
                    $"monero-wallet-rpc {archive.Version} is already installed and in use. Lock your wallet and try again.", ex);
            }
        }

        var manifest = new Dictionary<string, string>
        {
            ["version"] = archive.Version,
            ["archive"] = archive.FileName,
            ["archiveSha256"] = Convert.ToHexString(archiveSha256).ToLowerInvariant(),
            ["binarySha256"] = Convert.ToHexString(binarySha256).ToLowerInvariant(),
            ["signedHashList"] = HashListUrl.ToString(),
            ["installedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        PrivateFiles.WriteAllText(System.IO.Path.Combine(versionDir, ManifestName),
            JsonSerializer.Serialize(manifest, ManifestJson));
        return target;
    }

    /// <summary>Older installs are removed once a newer one works. One that is running (an open
    /// wallet still uses it) can't be deleted on Windows; it is left for next time.</summary>
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
