using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace XaultWallet.Core.Monero;

/// <summary>
/// Safety checks around the session folders monero-wallet-rpc works in (xaultwallet_*): where they
/// live, whether a folder found at startup is really ours, and whether the session that made it is
/// still alive. The startup sweep shreds what it finds, so it must never be steerable by another
/// local user planting a link or a look-alike folder in a shared directory such as /tmp.
/// </summary>
internal static class SessionHygiene
{
    /// <summary>Name of the file in each session folder that records the app's and the child's PIDs.</summary>
    internal const string PidFileName = "session.pid";

    private const UnixFileMode GroupOtherAny =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static readonly string[] StatPaths = ["/usr/bin/stat", "/bin/stat"];
    private static readonly string[] SetprivPaths = ["/usr/bin/setpriv", "/bin/setpriv"];
    private static readonly string[] SetprivProbeArgs = ["--pdeathsig", "SIGKILL", "--", "{self}", "--version"];

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    /// <summary>
    /// The folder session directories are created in. On Linux, the per-user runtime directory
    /// ($XDG_RUNTIME_DIR, normally /run/user/&lt;uid&gt;: private to the user and in memory) when it is
    /// usable; otherwise the shared temp folder, as before.
    /// </summary>
    internal static string SessionRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime) && Path.IsPathFullyQualified(runtime) && IsPrivateOwnDirectory(runtime))
            {
                return runtime;
            }
        }

        return Path.GetTempPath();
    }

    /// <summary>Every folder a session directory may sit in: the current root, and the temp folder
    /// older versions used.</summary>
    internal static IReadOnlyList<string> SweepRoots()
    {
        var roots = new List<string> { SessionRoot() };
        string temp = Path.GetTempPath();
        if (!roots.Contains(temp, StringComparer.Ordinal))
        {
            roots.Add(temp);
        }

        return roots;
    }

    /// <summary>
    /// A session directory the sweep may touch: a real directory (not a symlink or reparse point),
    /// and on Unix owned by the current user and not accessible to group or others. Fails closed:
    /// anything that can't be established is "no".
    /// </summary>
    internal static bool IsSafeSessionDir(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists || Security.SecureDelete.IsLink(info))
            {
                return false;
            }

            return OperatingSystem.IsWindows() || IsPrivateOwnDirectory(dir);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivateOwnDirectory(string dir)
    {
        try
        {
            if (OperatingSystem.IsWindows() || Security.SecureDelete.IsLink(new DirectoryInfo(dir)))
            {
                return false;
            }

            return (File.GetUnixFileMode(dir) & GroupOtherAny) == 0 && OwnerUid(dir) == GetEuid();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Owner of a path via the system's stat(1) (no struct layout to get wrong across
    /// libc/arch combinations); null when it can't be determined.</summary>
    private static uint? OwnerUid(string path)
    {
        string? stat = StatPaths.FirstOrDefault(File.Exists);
        if (stat is null)
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = stat,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-f" : "-c");
        psi.ArgumentList.Add("%u");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(path);

        using Process? p = Process.Start(psi);
        if (p is null)
        {
            return null;
        }

        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        return p.WaitForExit(5000) && p.ExitCode == 0 && uint.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint uid)
            ? uid
            : null;
    }

    /// <summary>What the PIDs recorded in a session folder say about its session.</summary>
    internal enum SessionState
    {
        /// <summary>No usable record: fall back on the age guard.</summary>
        Unknown,

        /// <summary>The app that made it is still running (another instance): leave it alone.</summary>
        OwnerAlive,

        /// <summary>The app is gone (a crash orphan). The child may still be running.</summary>
        Orphaned,
    }

    /// <summary>
    /// Read <see cref="PidFileName"/>. When the owning app is gone but wallet-rpc still runs (killed
    /// parent on a platform without a job object), that child is killed first, so its open wallet
    /// doesn't outlive the app and the folder can be shredded.
    /// </summary>
    internal static SessionState Inspect(string dir)
    {
        int appPid, childPid;
        try
        {
            string[] parts = File.ReadAllText(Path.Combine(dir, PidFileName)).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            appPid = ParsePid(parts, "app=");
            childPid = ParsePid(parts, "child=");
        }
        catch
        {
            return SessionState.Unknown;
        }

        if (appPid <= 0 || childPid <= 0)
        {
            return SessionState.Unknown;
        }

        if (appPid == Environment.ProcessId || IsRunning(appPid, Process.GetCurrentProcess().ProcessName))
        {
            return SessionState.OwnerAlive;
        }

        KillIfWalletRpc(childPid);
        return SessionState.Orphaned;
    }

    private static int ParsePid(string[] parts, string prefix)
    {
        string? part = parts.FirstOrDefault(p => p.StartsWith(prefix, StringComparison.Ordinal));
        return part is not null && int.TryParse(part[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ? pid : 0;
    }

    /// <summary>True when the process exists and still has the expected name (a recycled PID usually
    /// has another one).</summary>
    private static bool IsRunning(int pid, string expectedName)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited && string.Equals(p.ProcessName, expectedName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void KillIfWalletRpc(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            // Linux truncates the name to 15 characters ("monero-wallet-r"): match the stable prefix.
            if (!p.HasExited && p.ProcessName.StartsWith("monero-wallet", StringComparison.OrdinalIgnoreCase))
            {
                p.Kill();
                p.WaitForExit(5000);
            }
        }
        catch
        {
            // already gone, or not ours to kill
        }
    }

    private static readonly Lazy<string?> Setpriv = new(ProbeSetpriv);

    /// <summary>Absolute path of a working <c>setpriv</c> (util-linux) on Linux, else null. It lets the
    /// kernel kill wallet-rpc when the app dies for any reason (<c>--pdeathsig SIGKILL</c>).</summary>
    internal static string? SetprivPath => Setpriv.Value;

    private static string? ProbeSetpriv()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        string? path = SetprivPaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo { FileName = path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string a in SetprivProbeArgs)
            {
                psi.ArgumentList.Add(a == "{self}" ? path : a);
            }

            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(5000) && p.ExitCode == 0 ? path : null;
        }
        catch
        {
            return null;
        }
    }
}
