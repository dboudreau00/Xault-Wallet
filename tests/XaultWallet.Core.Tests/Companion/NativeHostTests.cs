using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using XaultWallet.Core.Companion;
using Xunit;

namespace XaultWallet.Core.Tests.Companion;

/// <summary>The native messaging host: the browsers' framing, who may start it, and what it passes on.</summary>
public class NativeHostTests
{
    private const string Address = "44AFFq5kSiGBoZ4NMDwYtN18obc8AemS33DBLWs3H7otXft3XjrpDtQGv7SqSsaBYBb98uNbr2VBBEt7f2wfn3RVGQBEP3A";

    private static byte[] Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] framed = new byte[body.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)body.Length);
        body.CopyTo(framed, 4);
        return framed;
    }

    private static List<JsonElement> Replies(byte[] output)
    {
        var replies = new List<JsonElement>();
        int at = 0;
        while (at < output.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(at));
            replies.Add(JsonDocument.Parse(output.AsMemory(at + 4, length)).RootElement.Clone());
            at += 4 + length;
        }

        return replies;
    }

    [Fact]
    public async Task Framing_Round_Trips_And_Ends_Cleanly()
    {
        var stream = new MemoryStream();
        await NativeMessaging.WriteAsync(stream, "{\"a\":1}"u8.ToArray());
        await NativeMessaging.WriteAsync(stream, "{}"u8.ToArray());
        stream.Position = 0;

        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString((await NativeMessaging.ReadAsync(stream))!));
        Assert.Equal("{}", Encoding.UTF8.GetString((await NativeMessaging.ReadAsync(stream))!));
        Assert.Null(await NativeMessaging.ReadAsync(stream));
    }

    [Fact]
    public async Task Oversized_Or_Truncated_Messages_Throw()
    {
        byte[] huge = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(huge, NativeMessaging.MaxIncomingBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(new MemoryStream(huge)));

        byte[] cut = Frame("{\"v\":1}")[..6];
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(new MemoryStream(cut)));
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(new MemoryStream(new byte[] { 1, 0 })));
    }

    [Fact]
    public async Task Lines_Are_Bounded()
    {
        var ok = new MemoryStream();
        await CompanionWire.WriteLineAsync(ok, "{\"x\":1}"u8.ToArray());
        ok.Position = 0;
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString((await CompanionWire.ReadLineAsync(ok, 100))!));
        Assert.Null(await CompanionWire.ReadLineAsync(ok, 100));

        await Assert.ThrowsAsync<InvalidDataException>(() => CompanionWire.ReadLineAsync(new MemoryStream(new byte[200]), 100));
        await Assert.ThrowsAsync<InvalidDataException>(() => CompanionWire.ReadLineAsync(new MemoryStream("{}"u8.ToArray()), 100));
        await Assert.ThrowsAsync<ArgumentException>(() => CompanionWire.WriteLineAsync(new MemoryStream(), "a\nb"u8.ToArray()));
    }

    [Theory]
    [InlineData(new[] { "chrome-extension://kpcmnhbbahdifkhgibnboafelgfkgcdj/" }, CompanionCaller.Chrome)]
    [InlineData(new[] { "chrome-extension://kpcmnhbbahdifkhgibnboafelgfkgcdj/", "--parent-window=0" }, CompanionCaller.Chrome)]
    [InlineData(new[] { "C:\\x\\dev.dboudreau.xaultwallet.firefox.json", "xaultwallet-companion@dboudreau.dev" }, CompanionCaller.Firefox)]
    [InlineData(new[] { "/home/u/.mozilla/native-messaging-hosts/dev.dboudreau.xaultwallet.json", "xaultwallet-companion@dboudreau.dev" }, CompanionCaller.Firefox)]
    public void The_Allowed_Extensions_Are_Recognised(string[] args, CompanionCaller expected)
    {
        Assert.Equal(expected, NativeHost.TryIdentifyCaller(args, out bool host));
        Assert.True(host);
    }

    [Theory]
    [InlineData(new[] { "chrome-extension://aaaabbbbccccddddeeeeffffgggghhhh/" }, true)]
    [InlineData(new[] { "chrome-extension://kpcmnhbbahdifkhgibnboafelgfkgcdj" }, true)]
    [InlineData(new[] { "chrome-extension://kpcmnhbbahdifkhgibnboafelgfkgcdj/", "--something-else" }, true)]
    [InlineData(new[] { "/tmp/x.json", "evil@example.com" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "monero:" + Address }, false)]
    [InlineData(new[] { "--help" }, false)]
    public void Anything_Else_Is_Refused(string[] args, bool looksLikeHost)
    {
        Assert.Null(NativeHost.TryIdentifyCaller(args, out bool host));
        Assert.Equal(looksLikeHost, host);
    }

    [Fact]
    public async Task Requests_Reach_The_App_With_The_Caller_The_Host_Established()
    {
        string? seen = null;
        var host = new NativeHost(CompanionCaller.Firefox,
            (line, _) =>
            {
                seen = Encoding.UTF8.GetString(line);
                return Task.FromResult<byte[]?>(CompanionProtocol.Ok("r1", w => w.WriteString("state", "locked")));
            },
            launchApp: () => throw new InvalidOperationException("must not start the app for a status request"));

        var output = new MemoryStream();
        int code = await host.RunAsync(new MemoryStream(Frame("""{"v":1,"id":"r1","type":"status","via":"chrome"}""")), output);

        Assert.Equal(0, code);
        Assert.Equal("""{"v":1,"id":"r1","type":"status","via":"firefox"}""", seen);
        JsonElement reply = Assert.Single(Replies(output.ToArray()));
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("locked", reply.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Bad_Requests_Are_Answered_Without_Bothering_The_App()
    {
        var host = new NativeHost(CompanionCaller.Chrome,
            (_, _) => throw new InvalidOperationException("must not be forwarded"),
            launchApp: () => false);

        var output = new MemoryStream();
        await host.RunAsync(new MemoryStream(Frame("""{"v":1,"id":"q","type":"sign","tx":"..."}""")), output);

        JsonElement reply = Assert.Single(Replies(output.ToArray()));
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("q", reply.GetProperty("id").GetString());
        Assert.Equal(CompanionErrors.UnknownType, reply.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_Payment_Starts_The_App_When_It_Is_Not_Running()
    {
        bool started = false;
        int attempts = 0;
        var host = new NativeHost(CompanionCaller.Chrome,
            (_, _) =>
            {
                attempts++;
                return Task.FromResult(started && attempts >= 3 ? CompanionProtocol.Ok("p", w => w.WriteString("result", "after-unlock")) : null);
            },
            launchApp: () => started = true,
            startWait: TimeSpan.FromSeconds(10));

        byte[] reply = await host.HandleAsync(Encoding.UTF8.GetBytes($$"""{"v":1,"id":"p","type":"pay","uri":"monero:{{Address}}"}"""));

        Assert.True(started);
        Assert.True(JsonDocument.Parse(reply).RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Status_And_Receive_Never_Start_The_App()
    {
        var host = new NativeHost(CompanionCaller.Chrome,
            (_, _) => Task.FromResult<byte[]?>(null),
            launchApp: () => throw new InvalidOperationException("must not start the app"));

        foreach (string type in new[] { "status", "receive" })
        {
            JsonElement reply = JsonDocument.Parse(await host.HandleAsync(Encoding.UTF8.GetBytes($$"""{"v":1,"id":"x","type":"{{type}}"}"""))).RootElement;
            Assert.Equal(CompanionErrors.NotRunning, reply.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task A_Reply_That_Is_Not_For_This_Request_Is_Not_Passed_On()
    {
        var host = new NativeHost(CompanionCaller.Chrome,
            (_, _) => Task.FromResult<byte[]?>(CompanionProtocol.Ok("someone-else", w => w.WriteString("address", Address))),
            launchApp: () => false);

        JsonElement reply = JsonDocument.Parse(await host.HandleAsync("""{"v":1,"id":"mine","type":"receive"}"""u8.ToArray())).RootElement;
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("mine", reply.GetProperty("id").GetString());
        Assert.False(reply.TryGetProperty("address", out _));
    }

    [Fact]
    public async Task An_Oversized_Frame_Gets_One_Error_And_Ends_The_Host()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        var host = new NativeHost(CompanionCaller.Chrome, (_, _) => Task.FromResult<byte[]?>(null), () => false);
        var output = new MemoryStream();

        Assert.Equal(1, await host.RunAsync(new MemoryStream(header), output));
        Assert.Equal(CompanionErrors.TooLarge, Assert.Single(Replies(output.ToArray())).GetProperty("error").GetProperty("code").GetString());
    }
}
