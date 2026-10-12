using XaultWallet.Core.Monero;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>The startup sweep shreds what it finds in a shared temp folder, so it must not be steerable
/// through links or look-alike folders, and a shredder must never follow a link out of its folder.</summary>
public class SessionHygieneTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("xwhygiene_").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Shredding_A_Folder_Never_Overwrites_What_A_Link_Inside_It_Points_To()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating a symlink needs a privilege there
        }

        string outside = Path.Combine(_dir, "precious.txt");
        File.WriteAllText(outside, "keep me");
        string session = Directory.CreateDirectory(Path.Combine(_dir, "session")).FullName;
        File.WriteAllText(Path.Combine(session, "own.txt"), "shred me");
        File.CreateSymbolicLink(Path.Combine(session, "link.txt"), outside);

        Assert.True(SecureDelete.Directory(session));
        Assert.Equal("keep me", File.ReadAllText(outside));
    }

    [Fact]
    public void A_Symlinked_Session_Folder_Is_Removed_As_A_Link_And_Its_Target_Is_Left_Alone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string target = Directory.CreateDirectory(Path.Combine(_dir, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "file.txt"), "keep me");
        string link = Path.Combine(_dir, "xaultwallet_link");
        Directory.CreateSymbolicLink(link, target);

        Assert.False(SessionHygiene.IsSafeSessionDir(link));
        SecureDelete.Directory(link);
        Assert.False(Directory.Exists(link));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(target, "file.txt")));
    }

    [Fact]
    public void A_Group_Or_World_Accessible_Folder_Is_Not_A_Safe_Session_Folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string own = Directory.CreateDirectory(Path.Combine(_dir, "xaultwallet_own")).FullName;
        File.SetUnixFileMode(own, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.True(SessionHygiene.IsSafeSessionDir(own));

        File.SetUnixFileMode(own, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        Assert.False(SessionHygiene.IsSafeSessionDir(own));
    }

    [Fact]
    public void A_Pid_Record_Of_A_Running_App_Marks_The_Session_As_Alive()
    {
        string session = Directory.CreateDirectory(Path.Combine(_dir, "xaultwallet_pid")).FullName;
        File.WriteAllText(Path.Combine(session, SessionHygiene.PidFileName), $"app={Environment.ProcessId} child=1\n");
        Assert.Equal(SessionHygiene.SessionState.OwnerAlive, SessionHygiene.Inspect(session));

        File.WriteAllText(Path.Combine(session, SessionHygiene.PidFileName), "garbage");
        Assert.Equal(SessionHygiene.SessionState.Unknown, SessionHygiene.Inspect(session));
    }
}
