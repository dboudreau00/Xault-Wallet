using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using XaultWallet.Core.Monero;

namespace XaultWallet.Core.Companion;

/// <summary>
/// Where the running app listens for the browser companion and for monero: links: a named pipe on
/// Windows, a Unix socket elsewhere. Only the same user can connect (on Windows only from this computer:
/// the pipe refuses network logons), and a client only accepts a server run by the same user
/// (<see cref="PipeOptions.CurrentUserOnly"/>, which checks the peer's identity on every platform). The
/// name is derived from the user, the login session and the app's data folder, so a second account, a
/// second session, or a test profile each get their own.
/// </summary>
public sealed class CompanionEndpoint
{
    private CompanionEndpoint(string pipeName) => PipeName = pipeName;

    /// <summary>The pipe's name on Windows; the socket's full path elsewhere.</summary>
    public string PipeName { get; }

    /// <summary>False where the channel can't be made: a Unix socket's path is limited to about 104 bytes,
    /// and a data folder deep enough to pass that leaves the companion and links off. They are never moved
    /// to the shared temp folder instead, where another account could take the name first.</summary>
    public bool IsUsable => OperatingSystem.IsWindows() || Encoding.UTF8.GetByteCount(PipeName) <= MaxSocketPathBytes;

    private const int MaxSocketPathBytes = 100;

    /// <summary>The endpoint of the app whose data folder is <paramref name="dataDirectory"/>.</summary>
    public static CompanionEndpoint ForDataDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (OperatingSystem.IsWindows())
        {
            string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            int session = Process.GetCurrentProcess().SessionId;
            string tag = Tag($"{user}|{session}|{full.ToUpperInvariant()}");
            return new CompanionEndpoint("XaultWallet.Companion." + tag);
        }

        // On Linux the per-user runtime folder (/run/user/<uid>, private and in memory) when it is
        // usable, else the app's own data folder, which is 0700. Never the shared temp folder, where
        // another account could take the name first. Socket paths are short (about 100 bytes).
        string runtime = SessionHygiene.SessionRoot();
        if (OperatingSystem.IsLinux() && !string.Equals(Path.TrimEndingDirectorySeparator(runtime), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.Ordinal))
        {
            return new CompanionEndpoint(Path.Combine(runtime, "xaultwallet-" + Tag(full) + ".sock"));
        }

        return new CompanionEndpoint(Path.Combine(full, "companion.sock"));
    }

    /// <summary>
    /// A listening end, for the app. <paramref name="first"/>: the app's first one, which on Windows fails
    /// if a pipe of that name already exists (another program got there first and would be answering in
    /// XaultWallet's place). Call <see cref="RemoveLeftoverSocket"/> once before the first.
    /// </summary>
    public NamedPipeServerStream CreateServer(int maxInstances, bool first = false)
    {
        if (OperatingSystem.IsWindows())
        {
            // What CurrentUserOnly sets up (this user, full control, as the owner), plus a refusal for
            // network logons: the same account signing in over the network is someone else's computer.
            PipeOptions windowsOptions = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
            return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, windowsOptions, 0, 0, ThisUserOnLocalLogons());
        }

        var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        // The folder is private already; the socket itself is made owner-only as well.
        try { File.SetUnixFileMode(PipeName, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return server;
    }

    [SupportedOSPlatform("windows")]
    private static PipeSecurity ThisUserOnLocalLogons()
    {
        // The owner and the allowed identity are the token's owner, as CurrentUserOnly sets them: a client with
        // CurrentUserOnly checks that owner against its own.
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = me.Owner ?? me.User ?? throw new UnauthorizedAccessException("No user identity for the companion channel.");
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    /// <summary>A connecting end, for the native host and for a second launch with a link.</summary>
    public NamedPipeClientStream CreateClient() =>
        new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>Unix: remove a socket file a crashed run left behind. Only the app calls this, while it
    /// holds the single-instance lock, so nothing else of ours can be listening there. Nothing on Windows,
    /// where a pipe goes away with the process that made it.</summary>
    public void RemoveLeftoverSocket()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var info = new FileInfo(PipeName);
            if (info.Exists || info.LinkTarget is not null)
            {
                info.Delete(); // a link is removed itself, never followed
            }
        }
        catch (IOException)
        {
            // creating the server reports it if the name is still taken
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Tag(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

/// <summary>Sends one request to the running app over its <see cref="CompanionEndpoint"/>.</summary>
public static class CompanionClient
{
    /// <summary>
    /// The app's reply to <paramref name="request"/>, or null when no app is listening within
    /// <paramref name="connectTimeout"/>. A reply that doesn't come within <paramref name="replyTimeout"/>
    /// throws <see cref="TimeoutException"/>. On Windows the app that answers may take the foreground
    /// (this process was started by the browser or the shell the user is clicking in; the right is
    /// passed to that one process, not to every program).
    /// </summary>
    public static async Task<byte[]?> SendAsync(CompanionEndpoint endpoint, byte[] request, TimeSpan connectTimeout, TimeSpan replyTimeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsUsable)
        {
            return null; // no app can be listening there
        }

        await using NamedPipeClientStream client = endpoint.CreateClient();
        try
        {
            await client.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null; // nobody there, or a server run by another user (refused by CurrentUserOnly)
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(client.SafePipeHandle, out uint server))
        {
            DetachedLauncher.AllowForeground((int)server);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(replyTimeout);
        try
        {
            await CompanionWire.WriteLineAsync(client, request, timeout.Token).ConfigureAwait(false);
            return await CompanionWire.ReadLineAsync(client, CompanionProtocol.MaxReplyBytes, timeout.Token).ConfigureAwait(false)
                   ?? throw new InvalidDataException("XaultWallet closed the connection without replying.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("XaultWallet didn't reply in time.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}
