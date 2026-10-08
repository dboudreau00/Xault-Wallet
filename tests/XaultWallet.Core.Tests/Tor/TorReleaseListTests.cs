using XaultWallet.Core.Tor;
using Xunit;

namespace XaultWallet.Core.Tests.Tor;

/// <summary>The unsigned update channel only picks a folder; the signed list alone names the checksum.</summary>
public sealed class TorReleaseListTests
{
    [Theory]
    [InlineData("""{"binary":"x","version":"15.0.24"}""", "15.0.24")]
    [InlineData("""{"version":"16.0"}""", "16.0")]
    [InlineData("""{"version":"15.0.24.1"}""", "15.0.24.1")]
    public void Reads_The_Current_Version(string json, string expected) =>
        Assert.Equal(expected, TorReleaseList.ParseCurrentVersion(json));

    [Theory]
    [InlineData("""{"version":"16.0a13"}""")]      // an alpha: not the release channel
    [InlineData("""{"version":"15.0.24/../x"}""")]  // ends up in a URL and a folder name
    [InlineData("""{"version":"15"}""")]
    [InlineData("""{"version":15.024}""")]
    [InlineData("""{"version":""}""")]
    [InlineData("""{"binary":"x"}""")]
    [InlineData("""["15.0.24"]""")]
    [InlineData("not json")]
    public void Refuses_Anything_But_A_Plain_Version(string json) =>
        Assert.Throws<InvalidOperationException>(() => TorReleaseList.ParseCurrentVersion(json));

    [Fact]
    public void Picks_This_Platforms_Bundle_For_The_Asked_Version()
    {
        string[] lines =
        [
            new string('a', 64) + "  tor-expert-bundle-linux-x86_64-15.0.24.tar.gz",
            new string('B', 64) + "  tor-expert-bundle-windows-x86_64-15.0.24.tar.gz",
            new string('c', 64) + " *tor-expert-bundle-macos-aarch64-15.0.24.tar.gz",
            "garbage line",
        ];

        Assert.Equal(new string('b', 64), TorReleaseList.Select(lines, "windows-x86_64", "15.0.24").Sha256);
        Assert.Equal(new string('c', 64), TorReleaseList.Select(lines, "macos-aarch64", "15.0.24").Sha256);
    }

    [Fact]
    public void A_List_From_Another_Release_Does_Not_Do()
    {
        string[] lines = [new string('a', 64) + "  tor-expert-bundle-linux-x86_64-15.0.23.tar.gz"];
        var ex = Assert.Throws<InvalidOperationException>(() => TorReleaseList.Select(lines, "linux-x86_64", "15.0.24"));
        Assert.Contains("no Tor Expert Bundle", ex.Message);
    }

    [Fact]
    public void A_Bundle_Named_Twice_Is_Refused()
    {
        string name = "  tor-expert-bundle-linux-x86_64-15.0.24.tar.gz";
        Assert.Throws<InvalidOperationException>(() =>
            TorReleaseList.Select([new string('a', 64) + name, new string('b', 64) + name], "linux-x86_64", "15.0.24"));
    }

    [Fact]
    public void macOS_Uses_The_Universal_Update_Entry()
    {
        Assert.Equal("download-macos.json", TorReleaseList.UpdateJsonName("macos-aarch64"));
        Assert.Equal("download-windows-x86_64.json", TorReleaseList.UpdateJsonName("windows-x86_64"));
        Assert.Equal("tor-expert-bundle-linux-x86_64-15.0.24.tar.gz", TorReleaseList.BundleFileName("linux-x86_64", "15.0.24"));
    }
}
