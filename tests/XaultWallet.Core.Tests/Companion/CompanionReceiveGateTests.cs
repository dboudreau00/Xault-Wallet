using XaultWallet.Core.Companion;
using Xunit;

namespace XaultWallet.Core.Tests.Companion;

/// <summary>A browser gets a new address only after the user allowed it in the app.</summary>
public sealed class CompanionReceiveGateTests
{
    private long _now = 1_000;
    private readonly object _wallet = new();

    private CompanionReceiveGate Gate() => new(() => _now);

    [Fact]
    public void The_first_request_only_asks()
    {
        var gate = Gate();
        Assert.False(gate.TryTake(_wallet, "shop.example"));
        Assert.Equal("shop.example", gate.AskedHost);
    }

    [Fact]
    public void After_the_user_allows_it_the_next_request_gets_one_address()
    {
        var gate = Gate();
        gate.TryTake(_wallet, "shop.example");
        Assert.True(gate.Allow());
        Assert.Null(gate.AskedHost);
        Assert.True(gate.TryTake(_wallet, "shop.example"));
        Assert.False(gate.TryTake(_wallet, "shop.example")); // used up: asks again
    }

    [Fact]
    public void An_allowance_is_for_that_site_and_that_wallet_only()
    {
        var gate = Gate();
        gate.TryTake(_wallet, "shop.example");
        gate.Allow();
        Assert.False(gate.TryTake(new object(), "shop.example"));
        Assert.False(gate.TryTake(_wallet, "other.example"));
    }

    [Fact]
    public void Nothing_waits_forever()
    {
        var gate = Gate();
        gate.TryTake(_wallet, "shop.example");
        _now += (long)CompanionReceiveGate.Lifetime.TotalMilliseconds + 1;
        Assert.Null(gate.AskedHost);
        Assert.False(gate.Allow());

        gate.TryTake(_wallet, "shop.example");
        gate.Allow();
        _now += (long)CompanionReceiveGate.Lifetime.TotalMilliseconds + 1;
        Assert.False(gate.TryTake(_wallet, "shop.example"));
    }

    [Fact]
    public void Allow_without_a_question_allows_nothing()
    {
        var gate = Gate();
        Assert.False(gate.Allow());
        Assert.False(gate.TryTake(_wallet, "shop.example"));
    }

    [Fact]
    public void Clear_drops_the_question_and_the_allowance()
    {
        var gate = Gate();
        gate.TryTake(_wallet, "shop.example");
        gate.Allow();
        gate.Clear();
        Assert.False(gate.TryTake(_wallet, "shop.example"));
    }

    [Theory]
    [InlineData("e2e shop", "https://shop.example/checkout", false, "")]
    [InlineData("", "https://shop.example/checkout", false, "")]
    [InlineData("e2e shop", "https://shop.example/checkout", true, "e2e shop")]
    [InlineData("", "https://shop.example/checkout", true, "shop.example")]
    [InlineData("", "", true, "no page")]
    public void Site_names_reach_the_vault_only_when_the_user_turned_that_on(string label, string origin, bool save, string expected) =>
        Assert.Equal(expected, CompanionReceiveGate.LabelFor(label, origin, save));
}
