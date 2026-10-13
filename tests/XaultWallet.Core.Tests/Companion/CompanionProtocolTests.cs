using System.Text;
using System.Text.Json;
using XaultWallet.Core.Companion;
using Xunit;

namespace XaultWallet.Core.Tests.Companion;

/// <summary>The browser companion's messages: strict on what comes in, exact on what goes out.</summary>
public class CompanionProtocolTests
{
    private const string Address = "44AFFq5kSiGBoZ4NMDwYtN18obc8AemS33DBLWs3H7otXft3XjrpDtQGv7SqSsaBYBb98uNbr2VBBEt7f2wfn3RVGQBEP3A";

    private static CompanionRequest? Parse(string json, CompanionCaller? via, out CompanionError? error) =>
        CompanionProtocol.Parse(Encoding.UTF8.GetBytes(json), via, out error);

    [Fact]
    public void A_Payment_Request_From_Chrome_Parses()
    {
        CompanionRequest? r = Parse($$"""{"v":1,"id":"a1","type":"pay","uri":"monero:{{Address}}?tx_amount=1.5","origin":"https://Shop.Example/checkout?x=1"}""", CompanionCaller.Chrome, out CompanionError? error);

        Assert.Null(error);
        Assert.Equal(new CompanionRequest("a1", CompanionRequestType.Pay, CompanionCaller.Chrome, $"monero:{Address}?tx_amount=1.5", "https://shop.example"), r);
    }

    [Fact]
    public void The_Browser_Does_Not_Get_To_Say_Who_It_Is()
    {
        CompanionRequest? r = Parse("""{"v":1,"id":"a","type":"status","via":"link"}""", CompanionCaller.Firefox, out _);
        Assert.Equal(CompanionCaller.Firefox, r!.Via);
    }

    [Fact]
    public void On_The_Local_Channel_Via_Is_Required()
    {
        Assert.Null(Parse("""{"v":1,"id":"a","type":"status"}""", via: null, out CompanionError? error));
        Assert.Equal(CompanionErrors.BadRequest, error!.Code);
        Assert.Equal(CompanionCaller.Link, Parse("""{"v":1,"id":"a","type":"status","via":"link"}""", via: null, out _)!.Via);
    }

    [Fact]
    public void Serialized_Requests_Parse_Back_Unchanged()
    {
        var request = new CompanionRequest("x-9_Z", CompanionRequestType.Receive, CompanionCaller.Chrome, Origin: "https://shop.example", Label: "Shop \"refunds\"");
        Assert.Equal(request, CompanionProtocol.Parse(CompanionProtocol.Serialize(request), via: null, out CompanionError? error));
        Assert.Null(error);
        Assert.DoesNotContain((byte)'\n', CompanionProtocol.Serialize(request));
    }

    [Theory]
    [InlineData("""[1,2]""", CompanionErrors.BadRequest)]
    [InlineData("""not json""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a","type":"status"} {}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":2,"id":"a","type":"status"}""", CompanionErrors.UnsupportedVersion)]
    [InlineData("""{"id":"a","type":"status"}""", CompanionErrors.UnsupportedVersion)]
    [InlineData("""{"v":"1","id":"a","type":"status"}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"","type":"status"}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a b","type":"status"}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a","type":"status","type":"pay"}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a","type":"sign"}""", CompanionErrors.UnknownType)]
    [InlineData("""{"v":1,"id":"a","type":"pay"}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a","type":"pay","uri":"bitcoin:1abc"}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a","type":"pay","uri":"monero:4x\nmore"}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a","type":"pay","uri":42}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a","type":"status","origin":"javascript:alert(1)"}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a","type":"status","origin":"https://user:pw@shop.example"}""", CompanionErrors.BadRequest)]
    [InlineData("""{"v":1,"id":"a","type":"receive","label":"0123456789012345678901234567890123456789X"}""", CompanionErrors.BadRequest)]
    public void Refuses_What_It_Should(string json, string code)
    {
        Assert.Null(Parse(json, CompanionCaller.Chrome, out CompanionError? error));
        Assert.Equal(code, error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Oversized_Requests_Are_Refused_Before_Parsing()
    {
        string json = "{\"v\":1,\"id\":\"a\",\"type\":\"status\",\"pad\":\"" + new string('x', CompanionProtocol.MaxRequestBytes) + "\"}";
        Assert.Null(Parse(json, CompanionCaller.Chrome, out CompanionError? error));
        Assert.Equal(CompanionErrors.TooLarge, error!.Code);
    }

    [Fact]
    public void Unknown_Fields_Of_A_Later_Version_Are_Ignored()
    {
        Assert.NotNull(Parse("""{"v":1,"id":"a","type":"status","extra":{"nested":[1,2,{"x":null}]}}""", CompanionCaller.Chrome, out _));
    }

    [Theory]
    [InlineData("https://shop.example/a/b?c=d#e", "https://shop.example")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    [InlineData("https://xn--80ak6aa92e.com/", "https://xn--80ak6aa92e.com")]
    [InlineData("https://аррӏе.com/", "https://xn--80ak6aa92e.com")] // a Cyrillic look-alike shows as punycode
    public void Origins_Are_Reduced_To_Scheme_And_Ascii_Host(string origin, string expected) =>
        Assert.Equal(expected, CompanionProtocol.NormalizeOrigin(origin));

    [Fact]
    public void Labels_Lose_Control_And_Direction_Characters()
    {
        Assert.Equal("Shop refunds", CompanionProtocol.CleanLabel("  Shop\t‮refunds\u0007  "));
        Assert.Equal(string.Empty, CompanionProtocol.CleanLabel(null));
        Assert.Null(CompanionProtocol.CleanLabel(new string('a', CompanionProtocol.MaxLabelLength + 1)));
    }

    [Fact]
    public void Replies_Are_Exact_Json()
    {
        Assert.Equal("""{"v":1,"id":"a","ok":true,"state":"locked"}""",
            Encoding.UTF8.GetString(CompanionProtocol.Ok("a", w => w.WriteString("state", "locked"))));
        Assert.Equal("""{"v":1,"id":"b","ok":false,"error":{"code":"locked","message":"Unlock XaultWallet first."}}""",
            Encoding.UTF8.GetString(CompanionProtocol.Fail("b", new CompanionError(CompanionErrors.Locked, "Unlock XaultWallet first."))));
    }

    [Fact]
    public void Only_A_Proper_Reply_To_This_Request_Is_Passed_On()
    {
        Assert.True(CompanionProtocol.IsReplyTo(CompanionProtocol.Ok("a"), "a"));
        Assert.False(CompanionProtocol.IsReplyTo(CompanionProtocol.Ok("a"), "b"));
        Assert.False(CompanionProtocol.IsReplyTo("""{"v":2,"id":"a","ok":true}"""u8, "a"));
        Assert.False(CompanionProtocol.IsReplyTo("""{"v":1,"id":"a"}"""u8, "a"));
        Assert.False(CompanionProtocol.IsReplyTo("""["v",1]"""u8, "a"));
        Assert.False(CompanionProtocol.IsReplyTo(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":\"a\",\"ok\":true,\"x\":\"" + new string('y', CompanionProtocol.MaxReplyBytes) + "\"}"), "a"));
    }

    [Fact]
    public void The_Id_Of_A_Bad_Request_Is_Still_Found_For_The_Reply()
    {
        Assert.Equal("abc", CompanionProtocol.TryReadId("""{"type":"nope","id":"abc"}"""u8));
        Assert.Equal(string.Empty, CompanionProtocol.TryReadId("""{"id":"has space"}"""u8));
        Assert.Equal(string.Empty, CompanionProtocol.TryReadId("""garbage"""u8));
    }

    [Fact]
    public void Error_Replies_Are_Valid_Json_Whatever_The_Message()
    {
        byte[] reply = CompanionProtocol.Fail("a", new CompanionError("x", "quote \" newline \n tab \t"));
        using JsonDocument doc = JsonDocument.Parse(reply);
        Assert.Equal("quote \" newline \n tab \t", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain((byte)'\n', reply);
    }
}
