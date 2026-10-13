using System.Text;
using XaultWallet.Core.Companion;
using Xunit;

namespace XaultWallet.Core.Tests.Companion;

/// <summary>The local channel itself: a real pipe (Windows) or socket (elsewhere), same user only.</summary>
public sealed class CompanionEndpointTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("xw-ep-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Each_Data_Folder_Gets_Its_Own_Endpoint()
    {
        string other = Path.Combine(_data, "other");
        Assert.NotEqual(CompanionEndpoint.ForDataDirectory(_data).PipeName, CompanionEndpoint.ForDataDirectory(other).PipeName);
        Assert.Equal(CompanionEndpoint.ForDataDirectory(_data).PipeName, CompanionEndpoint.ForDataDirectory(_data + Path.DirectorySeparatorChar).PipeName);
        if (!OperatingSystem.IsWindows())
        {
            string name = CompanionEndpoint.ForDataDirectory(_data).PipeName;
            Assert.True(Path.IsPathFullyQualified(name));
            Assert.True(Encoding.UTF8.GetByteCount(name) < 104, "a Unix socket path must stay short: " + name);
            Assert.False(name.StartsWith(Path.GetTempPath(), StringComparison.Ordinal) && !name.StartsWith(_data, StringComparison.Ordinal),
                "the socket must not sit in the shared temp folder: " + name);
        }
    }

    [Fact]
    public async Task A_Request_Line_Gets_Its_Reply_Line()
    {
        CompanionEndpoint endpoint = CompanionEndpoint.ForDataDirectory(_data);
        await using var server = endpoint.CreateServer(maxInstances: 1);
        Task serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            byte[]? line = await CompanionWire.ReadLineAsync(server, CompanionProtocol.MaxRequestBytes);
            await CompanionWire.WriteLineAsync(server, Encoding.UTF8.GetBytes("{\"echo\":" + Encoding.UTF8.GetString(line!) + "}"));
        });

        byte[]? reply = await CompanionClient.SendAsync(endpoint, "{\"v\":1}"u8.ToArray(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await serve;

        Assert.Equal("{\"echo\":{\"v\":1}}", Encoding.UTF8.GetString(reply!));
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(endpoint.PipeName);
            Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
        }
    }

    [Fact]
    public async Task On_Windows_The_First_Instance_Fails_When_The_Name_Is_Taken()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CompanionEndpoint endpoint = CompanionEndpoint.ForDataDirectory(_data);
        await using var squatter = endpoint.CreateServer(maxInstances: 4);
        Exception refused = Assert.ThrowsAny<Exception>(() => endpoint.CreateServer(maxInstances: 4, first: true).Dispose());
        Assert.True(refused is UnauthorizedAccessException or IOException, refused.GetType().Name);
        endpoint.CreateServer(maxInstances: 4).Dispose(); // a further instance (not the first) is fine
    }

    [Fact]
    public async Task Nobody_Listening_Is_Null_Not_An_Error()
    {
        CompanionEndpoint endpoint = CompanionEndpoint.ForDataDirectory(Path.Combine(_data, "nobody-here"));
        Assert.Null(await CompanionClient.SendAsync(endpoint, "{}"u8.ToArray(), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_Server_That_Never_Replies_Times_Out()
    {
        CompanionEndpoint endpoint = CompanionEndpoint.ForDataDirectory(_data);
        await using var server = endpoint.CreateServer(maxInstances: 1);
        Task accept = server.WaitForConnectionAsync();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            CompanionClient.SendAsync(endpoint, "{}"u8.ToArray(), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(400)));
        await accept;
    }

    [Fact]
    public async Task A_Socket_Left_By_A_Crash_Does_Not_Block_The_Next_Start()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // a pipe goes away with the process that made it
        }

        CompanionEndpoint endpoint = CompanionEndpoint.ForDataDirectory(_data);
        File.WriteAllText(endpoint.PipeName, "left behind");
        endpoint.RemoveLeftoverSocket();
        await using var server = endpoint.CreateServer(maxInstances: 1, first: true);
        Assert.True(File.Exists(endpoint.PipeName));
    }
}
