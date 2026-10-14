using XaultWallet.Core.Companion;
using Xunit;

namespace XaultWallet.Core.Tests.Companion;

/// <summary>The channel answers browsers only once the user connected them; a link hand-off always works.</summary>
public sealed class CompanionAccessTests
{
    private static CompanionRequest R(CompanionRequestType type, CompanionCaller via) => new("a", type, via);

    [Theory]
    [InlineData(CompanionRequestType.Status)]
    [InlineData(CompanionRequestType.Show)]
    [InlineData(CompanionRequestType.Pay)]
    [InlineData(CompanionRequestType.Receive)]
    public void Off_By_Default_For_Every_Browser_Command(CompanionRequestType type)
    {
        Assert.Equal(CompanionAccessDecision.NotConnected, CompanionAccess.Decide(R(type, CompanionCaller.Chrome), []));
        Assert.Equal(CompanionAccessDecision.NotConnected, CompanionAccess.Decide(R(type, CompanionCaller.Firefox), []));
    }

    [Fact]
    public void A_Connected_Browser_Is_Answered_And_Only_That_One()
    {
        string[] connected = ["chrome"];
        Assert.Equal(CompanionAccessDecision.Allow, CompanionAccess.Decide(R(CompanionRequestType.Receive, CompanionCaller.Chrome), connected));
        Assert.Equal(CompanionAccessDecision.NotConnected, CompanionAccess.Decide(R(CompanionRequestType.Status, CompanionCaller.Firefox), connected));
    }

    [Theory]
    [InlineData(CompanionRequestType.Pay)]
    [InlineData(CompanionRequestType.Show)]
    public void A_Link_Hand_Off_Works_Without_Any_Browser(CompanionRequestType type) =>
        Assert.Equal(CompanionAccessDecision.Allow, CompanionAccess.Decide(R(type, CompanionCaller.Link), []));

    [Theory]
    [InlineData(CompanionRequestType.Status)]
    [InlineData(CompanionRequestType.Receive)]
    public void A_Link_Can_Never_Ask_For_State_Or_Addresses(CompanionRequestType type) =>
        Assert.Equal(CompanionAccessDecision.NotConnected, CompanionAccess.Decide(R(type, CompanionCaller.Link), ["chrome", "firefox"]));

    [Fact]
    public void The_New_Refusal_Is_A_Known_Code() => Assert.Contains(CompanionErrors.NotConnected, CompanionErrors.All);
}
