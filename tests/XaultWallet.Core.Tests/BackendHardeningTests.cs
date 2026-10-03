using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using XaultWallet.Core.Monero;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// Before a seed goes to the wallet-rpc port, the app checks the listener really is its child.
/// A process that grabbed the port first would otherwise receive restore_deterministic_wallet.
/// </summary>
public class LoopbackPortOwnershipTests
{
    private static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    [Fact]
    public void Recognises_A_Listener_Owned_By_The_Given_Process()
    {
        if (!Supported)
        {
            return; // macOS: the check reports "can't tell" (null) by design
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(LoopbackPortOwnership.IsListenerOwnedBy(Environment.ProcessId, port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Rejects_A_Listener_Owned_By_Someone_Else()
    {
        if (!Supported)
        {
            return;
        }

        // A live process we own (so its fd table is readable without privileges) that does NOT hold
        // the socket: .NET creates sockets non-inheritable, so the child never gets our listener.
        ProcessStartInfo psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
            : new ProcessStartInfo("sleep", "30");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using Process other = Process.Start(psi)!;
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.False(LoopbackPortOwnership.IsListenerOwnedBy(other.Id, port));
        }
        finally
        {
            listener.Stop();
            try { other.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    [Fact]
    public void Reports_False_When_Nothing_Listens()
    {
        if (!Supported)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.False(LoopbackPortOwnership.IsListenerOwnedBy(Environment.ProcessId, port));
    }
}

public class ExecutableLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("xwlocator_").FullName;

    [Fact]
    public void Finds_The_Binary_In_An_Absolute_Path_Entry()
    {
        string exe = Path.Combine(_dir, "fake-wallet-rpc");
        File.WriteAllText(exe, "");
        string path = string.Join(Path.PathSeparator, "/definitely/missing", "", $"\"{_dir}\"");

        Assert.Equal(exe, ExecutableLocator.FindOnPath("fake-wallet-rpc", path));
    }

    [Fact]
    public void Skips_Relative_Path_Entries()
    {
        // A relative PATH entry resolves against the current directory — the exact lookup the
        // locator exists to avoid for a binary that handles keys.
        string name = $"xw-relative-{Guid.NewGuid():N}";
        string inCwd = Path.Combine(Directory.GetCurrentDirectory(), name);
        File.WriteAllText(inCwd, ""); // it really IS reachable through "." ...
        try
        {
            Assert.Null(ExecutableLocator.FindOnPath(name, "." + Path.PathSeparator + "bin")); // ...and still not returned
        }
        finally
        {
            File.Delete(inCwd);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_Path_Finds_Nothing(string? path) =>
        Assert.Null(ExecutableLocator.FindOnPath("monero-wallet-rpc", path));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup only */ }
    }
}

public class StderrScrubTests
{
    [Fact]
    public void Masks_Addresses_And_Key_Sized_Hex()
    {
        string address = "4" + new string('A', 94);
        string key = new('a', 64);
        string line = $"W wallet {address} key {key} done";

        string scrubbed = MoneroProcessManager.ScrubLine(line);

        Assert.DoesNotContain(address, scrubbed);
        Assert.DoesNotContain(key, scrubbed);
        Assert.Contains("done", scrubbed);
    }
}
