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

/// <summary>A connected browser's extension is asked about once, in the app, then remembered.</summary>
public sealed class CompanionPairingTests
{
    private static readonly string[] Chrome = ["chrome"];
    private static readonly string ChromeKey = CompanionAccess.PairingKey(CompanionCaller.Chrome)!;

    private static CompanionAccessDecision D(CompanionCaller via, CompanionRequestType type, string[] connected, string[] allowed, string[] refused) =>
        CompanionAccess.Decide(new CompanionRequest("a", type, via), connected, allowed, refused);

    [Fact]
    public void The_First_Contact_Asks_The_User() =>
        Assert.Equal(CompanionAccessDecision.NeedsPairing, D(CompanionCaller.Chrome, CompanionRequestType.Status, Chrome, [], []));

    [Fact]
    public void An_Allowed_Extension_Is_Answered() =>
        Assert.Equal(CompanionAccessDecision.Allow, D(CompanionCaller.Chrome, CompanionRequestType.Receive, Chrome, [ChromeKey], []));

    [Fact]
    public void A_Refused_Extension_Is_Refused_Without_Asking_Even_If_Also_Allowed() =>
        Assert.Equal(CompanionAccessDecision.Refused, D(CompanionCaller.Chrome, CompanionRequestType.Status, Chrome, [ChromeKey], [ChromeKey]));

    [Fact]
    public void Pairing_Never_Opens_A_Browser_That_Isnt_Connected() =>
        Assert.Equal(CompanionAccessDecision.NotConnected, D(CompanionCaller.Chrome, CompanionRequestType.Status, [], [ChromeKey], []));

    [Fact]
    public void A_Link_Needs_No_Pairing() =>
        Assert.Equal(CompanionAccessDecision.Allow, D(CompanionCaller.Link, CompanionRequestType.Pay, [], [], []));

    [Fact]
    public void Each_Browser_Is_Paired_On_Its_Own_And_Names_Its_Extension_Id()
    {
        string firefox = CompanionAccess.PairingKey(CompanionCaller.Firefox)!;
        Assert.NotEqual(ChromeKey, firefox);
        Assert.Contains("kpcmnhbbahdifkhgibnboafelgfkgcdj", ChromeKey, StringComparison.Ordinal);
        Assert.Equal(CompanionAccessDecision.NeedsPairing, D(CompanionCaller.Firefox, CompanionRequestType.Status, ["chrome", "firefox"], [ChromeKey], []));
        Assert.Null(CompanionAccess.PairingKey(CompanionCaller.Link));
    }
}
