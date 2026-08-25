using XaultWallet.Core.Monero;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// DaemonAddress is the single definition of "a valid node URL", enforced identically by the
/// create screen, the vault repoint path, and the process launcher.
/// </summary>
public class DaemonAddressTests
{
    [Theory]
    [InlineData("http://127.0.0.1:18081")]
    [InlineData("https://node.example.org:38089")]
    [InlineData("  http://spaced.example:28081  ")] // surrounding whitespace is tolerated
    public void Accepts_Absolute_Http_Urls(string address)
    {
        Assert.True(DaemonAddress.IsValid(address));
        Assert.True(DaemonAddress.TryParse(address, out var uri));
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("node.example:18081")]      // no scheme
    [InlineData("ftp://node.example:18081")] // wrong scheme
    [InlineData("file:///etc/passwd")]
    public void Rejects_Everything_Else(string? address)
    {
        Assert.False(DaemonAddress.IsValid(address));
        Assert.False(DaemonAddress.TryParse(address, out _));
    }
}
