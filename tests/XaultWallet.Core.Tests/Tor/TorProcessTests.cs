using XaultWallet.Core.Tor;
using Xunit;

namespace XaultWallet.Core.Tests.Tor;

/// <summary>Reading tor's log: how far it has got, and when the OS itself is refusing it.</summary>
public sealed class TorProcessTests
{
    [Theory]
    [InlineData("Oct 08 00:46:01.000 [notice] Bootstrapped 0% (starting): Starting", 0, "Starting")]
    [InlineData("Oct 08 00:46:03.000 [notice] Bootstrapped 45% (loading_descriptors): Loading relay descriptors", 45, "Loading relay descriptors")]
    [InlineData("Oct 08 00:46:09.000 [notice] Bootstrapped 100% (done): Done", 100, "Done")]
    [InlineData("May 01 10:00:00.000 [notice] Bootstrapped 80%: Connecting to the Tor network", 80, "Connecting to the Tor network")]
    public void Reads_Bootstrap_Progress(string line, int percent, string summary)
    {
        TorBootstrap? step = TorProcess.ParseBootstrap(line);
        Assert.NotNull(step);
        Assert.Equal(percent, step.Percent);
        Assert.Equal(summary, step.Summary);
    }

    [Theory]
    [InlineData("Oct 08 00:46:01.000 [notice] Opening Socks listener on 127.0.0.1:57396")]
    [InlineData("Bootstrapped 450% (nonsense): no")]
    [InlineData("")]
    public void Other_Lines_Are_Not_Progress(string line) => Assert.Null(TorProcess.ParseBootstrap(line));

    [Fact]
    public void A_Firewall_Refusal_Is_Recognised()
    {
        const string blocked = "Oct 08 00:47:12.000 [warn] Problem bootstrapping. Stuck at 0% (starting): Starting. " +
                               "(Permission denied [WSAEACCES ]; RESOURCELIMIT; count 18; recommendation warn; host D00E at 204.8.96.180:446)";
        const string slow = "Oct 08 00:47:12.000 [warn] Problem bootstrapping. Stuck at 14% (handshake): Handshaking with a relay. " +
                            "(Connection timed out; TIMEOUT; count 2; recommendation warn; host 6F09 at 64.65.62.59:443)";

        Assert.True(TorProcess.IsConnectRefusedLocally(blocked));
        Assert.False(TorProcess.IsConnectRefusedLocally(slow));
        Assert.True(TorProcess.RefusalsBeforeGivingUp > 1); // one refusal is a relay, several is the OS
    }

    [Fact]
    public void The_Socks_Address_Is_Loopback_On_A_Port_Chosen_Once()
    {
        var proc = new TorProcess(Path.Combine(Path.GetTempPath(), "no-tor-here"), Path.Combine(Path.GetTempPath(), "xw-tor-data"), socksPort: 9311);
        Assert.Equal("127.0.0.1:9311", proc.SocksAddress);

        var picked = new TorProcess(Path.Combine(Path.GetTempPath(), "no-tor-here"), Path.Combine(Path.GetTempPath(), "xw-tor-data"));
        Assert.InRange(picked.SocksPort, 1, 65535);
        Assert.StartsWith("127.0.0.1:", picked.SocksAddress, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Missing_Binary_Fails_Before_Anything_Starts()
    {
        var proc = new TorProcess("relative/tor", Path.Combine(Path.GetTempPath(), "xw-tor-data-" + Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAnyAsync<Exception>(() => proc.StartAsync(null, TimeSpan.FromSeconds(5), default));
        Assert.True(proc.HasExited);
    }
}
