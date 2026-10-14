using System.Text;
using System.Text.Json;
using XaultWallet.Core.Companion;
using Xunit;

namespace XaultWallet.Core.Tests.Companion;

/// <summary>The browser companion's messages: strict on what comes in, exact on what goes out.</summary>
public class CompanionProtocolTests
{
    private static string Ch(int codePoint) => UntrustedTextTests.Ch(codePoint);

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
    public void Limits_Hold_At_Their_Edges()
    {
        string id = new string('a', CompanionProtocol.MaxIdLength);
        Assert.NotNull(Parse($$"""{"v":1,"id":"{{id}}","type":"status"}""", CompanionCaller.Chrome, out _));
        Assert.Null(Parse($$"""{"v":1,"id":"{{id}}b","type":"status"}""", CompanionCaller.Chrome, out _));

        string head = "monero:" + Address + "?tx_description=";
        string longest = head + new string('x', CompanionProtocol.MaxUriLength - head.Length);
        Assert.NotNull(CompanionProtocol.CleanPaymentUri(longest));
        Assert.Null(CompanionProtocol.CleanPaymentUri(longest + "x"));

        string page = "https://shop.example/";
        Assert.Equal("https://shop.example", CompanionProtocol.NormalizeOrigin(page + new string('p', CompanionProtocol.MaxOriginLength - page.Length)));
        Assert.Null(CompanionProtocol.NormalizeOrigin(page + new string('p', CompanionProtocol.MaxOriginLength - page.Length + 1)));

        // Nesting: the request object and seven arrays in an unknown field pass; one more is refused.
        Assert.NotNull(Parse("""{"v":1,"id":"a","type":"status","x":[[[[[[[1]]]]]]]}""", CompanionCaller.Chrome, out _));
        Assert.Null(Parse("""{"v":1,"id":"a","type":"status","x":[[[[[[[[1]]]]]]]]}""", CompanionCaller.Chrome, out CompanionError? deep));
        Assert.Equal(CompanionErrors.BadRequest, deep!.Code);
    }

    [Fact]
    public async Task A_Line_May_Be_Exactly_The_Limit_And_No_Longer()
    {
        int max = CompanionProtocol.MaxRequestBytes;
        byte[] exact = new byte[max + 1];
        Array.Fill(exact, (byte)'x');
        exact[^1] = (byte)'\n';
        Assert.Equal(max, (await CompanionWire.ReadLineAsync(new MemoryStream(exact), max))!.Length);

        byte[] over = new byte[max + 2];
        Array.Fill(over, (byte)'x');
        over[^1] = (byte)'\n';
        await Assert.ThrowsAsync<InvalidDataException>(() => CompanionWire.ReadLineAsync(new MemoryStream(over), max));
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

    [Theory]
    [InlineData("http://[::1]:3000/x", "http://[::1]:3000")]
    [InlineData("https://[2001:db8::1]/", "https://[2001:db8::1]")]
    public void Ipv6_Origins_Keep_Their_Brackets_And_Round_Trip(string origin, string expected)
    {
        string? normalized = CompanionProtocol.NormalizeOrigin(origin);
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, CompanionProtocol.NormalizeOrigin(normalized));
        Assert.StartsWith("[", CompanionProtocol.HostOf(expected));
    }

    [Theory]
    [InlineData("http://shop.example:0/")]
    public void Odd_Origins_Are_Refused(string origin) => Assert.Null(CompanionProtocol.NormalizeOrigin(origin));

    [Fact]
    public void Origins_With_Hidden_Characters_Are_Refused() =>
        Assert.Null(CompanionProtocol.NormalizeOrigin("https://shop" + Ch(0x202E) + ".example/"));

    [Fact]
    public void Only_Plain_Http_Pages_Off_This_Computer_Are_Insecure()
    {
        Assert.True(CompanionProtocol.IsInsecureOrigin("http://shop.example"));
        Assert.False(CompanionProtocol.IsInsecureOrigin("https://shop.example"));
        Assert.False(CompanionProtocol.IsInsecureOrigin("http://localhost:8080"));
        Assert.False(CompanionProtocol.IsInsecureOrigin("http://[::1]:3000"));
    }

    [Fact]
    public void Labels_Lose_Control_And_Direction_Characters()
    {
        Assert.Equal("Shop refunds", CompanionProtocol.CleanLabel("  Shop\t" + Ch(0x202E) + "refunds" + Ch(0x07) + "  "));
        Assert.Equal("Shop refunds", CompanionProtocol.CleanLabel("Shop" + Ch(0x2028) + Ch(0x200B) + "refunds" + Ch(0xE0041)));
        Assert.Equal(string.Empty, CompanionProtocol.CleanLabel(null));
        Assert.Null(CompanionProtocol.CleanLabel(new string('a', CompanionProtocol.MaxLabelLength + 1)));
    }

    [Fact]
    public void Payment_Links_With_Hidden_Characters_Are_Refused()
    {
        // Right-to-left override, line separator, a tag character (outside the BMP), a lone surrogate, NEL.
        foreach (string hidden in new[] { Ch(0x202E), Ch(0x2028), Ch(0xE0041), Ch(0xD800), Ch(0x85) })
        {
            Assert.Null(CompanionProtocol.CleanPaymentUri("monero:" + Address + "?tx_description=a" + hidden + "b"));
        }

        Assert.NotNull(CompanionProtocol.CleanPaymentUri("monero:" + Address + "?tx_description=Rent%20July"));
        Assert.NotNull(CompanionProtocol.CleanPaymentUri("monero:" + Address + "?recipient_name=Café"));
        Assert.Null(CompanionProtocol.CleanPaymentUri("monero:" + new string('4', CompanionProtocol.MaxUriLength)));
    }

    [Fact]
    public void Invalid_Utf8_Is_A_Bad_Request_Not_A_Crash()
    {
        byte[] json = Encoding.UTF8.GetBytes("""{"v":1,"id":"a","type":"receive","label":"xx"}""");
        int at = Array.IndexOf(json, (byte)'x');
        json[at] = 0xC3; // a lead byte followed by an ASCII byte: not UTF-8
        Assert.Null(CompanionProtocol.Parse(json, CompanionCaller.Chrome, out CompanionError? error));
        Assert.Equal(CompanionErrors.BadRequest, error!.Code);
    }

    [Fact]
    public void Replies_Are_Exact_Json()
    {
        Assert.Equal("""{"v":1,"id":"a","ok":true,"state":"locked"}""",
            Encoding.UTF8.GetString(CompanionProtocol.Ok("a", w => w.WriteString("state", "locked"))));
        Assert.Equal("""{"v":1,"id":"b","ok":false,"error":{"code":"locked","message":"Unlock XaultWallet first."}}""",
            Encoding.UTF8.GetString(CompanionProtocol.Fail("b", new CompanionError(CompanionErrors.Locked, "Unlock XaultWallet first."))));
    }

    private static CompanionRequest Request(CompanionRequestType type, string id = "a") => new(id, type, CompanionCaller.Chrome);

    private static string? Rebuilt(string reply, CompanionRequestType type, string id = "a") =>
        CompanionProtocol.RebuildReply(Encoding.UTF8.GetBytes(reply), Request(type, id)) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    [Fact]
    public void Only_A_Proper_Reply_To_This_Request_Is_Passed_On()
    {
        Assert.Equal("""{"v":1,"id":"a","ok":true}""", Rebuilt("""{"v":1,"id":"a","ok":true}""", CompanionRequestType.Show));
        Assert.Null(Rebuilt("""{"v":1,"id":"a","ok":true}""", CompanionRequestType.Show, id: "b"));
        Assert.Null(Rebuilt("""{"v":2,"id":"a","ok":true}""", CompanionRequestType.Show));
        Assert.Null(Rebuilt("""{"v":1,"id":"a"}""", CompanionRequestType.Show));
        Assert.Null(Rebuilt("""["v",1]""", CompanionRequestType.Show));
        Assert.Null(Rebuilt("{\"v\":1,\"id\":\"a\",\"ok\":true,\"x\":\"" + new string('y', CompanionProtocol.MaxReplyBytes) + "\"}", CompanionRequestType.Show));
    }

    [Fact]
    public void Replies_Are_Rebuilt_From_The_Fields_Of_Their_Kind()
    {
        // Status: the state and "ready" only; the version, network and watch-only are dropped (a decoy that
        // differs from the real wallet in them would otherwise be told apart).
        Assert.Equal("""{"v":1,"id":"a","ok":true,"app":"XaultWallet","state":"unlocked","ready":true}""",
            Rebuilt("""{"v":1,"id":"a","ok":true,"app":"Other","version":"0.7.0-beta","state":"unlocked","network":"Stagenet","watchOnly":true,"ready":true,"balance":"12"}""", CompanionRequestType.Status));
        Assert.Equal("""{"v":1,"id":"a","ok":true,"app":"XaultWallet","state":"locked"}""",
            Rebuilt("""{"v":1,"id":"a","ok":true,"version":"<b>x</b>","state":"locked","ready":true}""", CompanionRequestType.Status));
        Assert.Equal("""{"v":1,"id":"a","ok":true,"app":"XaultWallet","state":"unlocked","ready":false}""",
            Rebuilt("""{"v":1,"id":"a","ok":true,"state":"unlocked","network":"Moon"}""", CompanionRequestType.Status));
        Assert.Null(Rebuilt("""{"v":1,"id":"a","ok":true,"state":"open"}""", CompanionRequestType.Status));

        Assert.Equal("""{"v":1,"id":"a","ok":true,"result":"shown"}""", Rebuilt("""{"v":1,"id":"a","ok":true,"result":"shown","x":1}""", CompanionRequestType.Pay));
        Assert.Null(Rebuilt("""{"v":1,"id":"a","ok":true,"result":"constructor"}""", CompanionRequestType.Pay));
        Assert.Equal("""{"v":1,"id":"a","ok":true,"result":"held"}""", Rebuilt("""{"v":1,"id":"a","ok":true,"result":"held"}""", CompanionRequestType.Pay));

        Assert.Equal($$"""{"v":1,"id":"a","ok":true,"address":"{{Address}}","network":"Mainnet"}""",
            Rebuilt($$"""{"v":1,"id":"a","ok":true,"address":"{{Address}}","network":"Mainnet","note":"hi"}""", CompanionRequestType.Receive));
        Assert.Null(Rebuilt($$"""{"v":1,"id":"a","ok":true,"address":"{{Address}}","network":"Stagenet"}""", CompanionRequestType.Receive));
        Assert.Null(Rebuilt("""{"v":1,"id":"a","ok":true,"network":"Mainnet"}""", CompanionRequestType.Receive));
        Assert.Null(Rebuilt($$"""{"v":1,"id":"a","ok":true,"address":"{{Address}}0123456789A","network":"Mainnet"}""", CompanionRequestType.Receive));
    }

    [Fact]
    public void Refusals_Keep_A_Known_Code_And_A_Clean_Message()
    {
        Assert.Equal("""{"v":1,"id":"a","ok":false,"error":{"code":"busy","message":"Finish it first. Then retry."}}""",
            Rebuilt("""{"v":1,"id":"a","ok":false,"error":{"code":"busy","message":"Finish it first.\n\u202EThen retry.","extra":1}}""", CompanionRequestType.Pay));
        Assert.Null(Rebuilt("""{"v":1,"id":"a","ok":false,"error":{"code":"pwned","message":"x"}}""", CompanionRequestType.Pay));
        Assert.Null(Rebuilt("""{"v":1,"id":"a","ok":false}""", CompanionRequestType.Pay));
    }

    [Fact]
    public void A_Reply_That_Is_Not_Utf8_Is_Dropped_Not_A_Crash()
    {
        byte[] reply = Encoding.UTF8.GetBytes("""{"v":1,"id":"a","ok":true,"result":"shown"}""");
        reply[^4] = 0xC3;
        Assert.Null(CompanionProtocol.RebuildReply(reply, Request(CompanionRequestType.Pay)));
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
