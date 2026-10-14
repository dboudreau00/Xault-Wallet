using XaultWallet.Core.Companion;
using Xunit;

namespace XaultWallet.Core.Tests.Companion;

/// <summary>The tripwire compares the program a connection leads to with the running app, byte for byte.</summary>
public sealed class ExecutableFingerprintTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("xw-fp-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_Copy_Matches_And_A_Changed_File_Does_Not()
    {
        string app = ExecutableFingerprint.Sha256(Write("a.exe", "XaultWallet build 1"))!;
        Assert.Equal(64, app.Length);
        Assert.Equal(FingerprintMatch.Same, ExecutableFingerprint.Compare(app, ExecutableFingerprint.Sha256(Write("copy.exe", "XaultWallet build 1"))));
        Assert.Equal(FingerprintMatch.Different, ExecutableFingerprint.Compare(app, ExecutableFingerprint.Sha256(Write("other.exe", "something else"))));
    }

    [Fact]
    public void A_File_Replaced_At_The_Same_Path_Is_Noticed()
    {
        string path = Write("x.exe", "the app as started");
        string? atStart = ExecutableFingerprint.Sha256(path);
        File.WriteAllText(path, "a stand-in");
        Assert.Equal(FingerprintMatch.Different, ExecutableFingerprint.Compare(atStart, ExecutableFingerprint.Sha256(path)));
    }

    [Fact]
    public void What_Cant_Be_Read_Is_Unknown_Never_A_Failure()
    {
        Assert.Null(ExecutableFingerprint.Sha256(Path.Combine(_dir, "missing.exe")));
        Assert.Null(ExecutableFingerprint.Sha256("relative.exe"));
        Assert.Null(ExecutableFingerprint.Sha256(null));
        Assert.Null(ExecutableFingerprint.Sha256(Write("empty.exe", "")));
        Assert.Null(ExecutableFingerprint.Sha256(_dir));
        Assert.Equal(FingerprintMatch.Unknown, ExecutableFingerprint.Compare(null, "ab"));
        Assert.Equal(FingerprintMatch.Unknown, ExecutableFingerprint.Compare("ab", null));
    }
}
