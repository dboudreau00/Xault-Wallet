using XaultWallet.Core.Diagnostics;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// Wallet material must be owner-only on Linux/macOS regardless of the umask (commonly 022, which
/// makes ordinary new files world-readable). Windows relies on per-user profile ACLs: no-op there.
/// </summary>
public class PrivateFilesTests : IDisposable
{
    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const UnixFileMode Loose =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly string _dir = Directory.CreateTempSubdirectory("xwperm_").FullName;

    [Fact]
    public void New_Files_Are_Owner_Only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(_dir, "secret.txt");
        PrivateFiles.WriteAllText(path, "seed words");
        Assert.Equal(PrivateFiles.OwnerFile, File.GetUnixFileMode(path));
    }

    [Fact]
    public void An_Existing_Loose_File_Is_Tightened_Before_It_Is_Rewritten()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(_dir, "picked-by-dialog.txt");
        File.WriteAllText(path, "");
        File.SetUnixFileMode(path, Loose); // what a save dialog may leave behind
        PrivateFiles.WriteAllText(path, "seed words");
        Assert.Equal(PrivateFiles.OwnerFile, File.GetUnixFileMode(path));
        Assert.Equal("seed words", File.ReadAllText(path));
    }

    [Fact]
    public void Folders_Are_Created_Or_Tightened_To_0700()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string fresh = Path.Combine(_dir, "fresh");
        PrivateFiles.EnsureDirectory(fresh);
        Assert.Equal(PrivateFiles.OwnerDirectory, File.GetUnixFileMode(fresh));

        string old = Directory.CreateDirectory(Path.Combine(_dir, "old")).FullName;
        File.SetUnixFileMode(old, PrivateFiles.OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        PrivateFiles.EnsureDirectory(old);
        Assert.Equal(PrivateFiles.OwnerDirectory, File.GetUnixFileMode(old));
    }

    [Fact]
    public void Startup_Sweep_Tightens_An_Old_Install_Without_Following_Symlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string data = Directory.CreateDirectory(Path.Combine(_dir, "XaultWallet")).FullName;
        string logs = Directory.CreateDirectory(Path.Combine(data, "logs")).FullName;
        string vault = Path.Combine(data, "vault.xv");
        string log = Path.Combine(logs, "xaultwallet.log");
        File.WriteAllText(vault, "v");
        File.WriteAllText(log, "l");
        File.SetUnixFileMode(vault, Loose);
        File.SetUnixFileMode(log, Loose);

        string outside = Path.Combine(_dir, "not-ours.txt");
        File.WriteAllText(outside, "x");
        File.SetUnixFileMode(outside, Loose);
        File.CreateSymbolicLink(Path.Combine(data, "link"), outside);

        PrivateFiles.TightenTree(data);

        Assert.Equal(PrivateFiles.OwnerDirectory, File.GetUnixFileMode(data));
        Assert.Equal(PrivateFiles.OwnerDirectory, File.GetUnixFileMode(logs));
        Assert.Equal(PrivateFiles.OwnerFile, File.GetUnixFileMode(vault));
        Assert.Equal(PrivateFiles.OwnerFile, File.GetUnixFileMode(log));
        Assert.Equal(Loose, File.GetUnixFileMode(outside)); // a symlink's target is left alone
    }

    [Fact]
    public void The_Vault_Is_Written_Owner_Only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(_dir, "vault.xv");
        using var pw = SecureBuffer.FromPassword("main-password-123".ToCharArray());
        VaultManager.Create(path, pw, new WalletSecrets { Mnemonic = "real seed words" },
            argon: new VaultCrypto.Argon2Parameters(19_456, 2, 1));

        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(path) & GroupOrOther);
    }

    [Fact]
    public void Log_Files_Are_Owner_Only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string logs = Path.Combine(_dir, "logs");
        Log.Initialize(logs);
        Log.Info("permission test");
        Assert.Equal(PrivateFiles.OwnerDirectory, File.GetUnixFileMode(logs));
        Assert.Equal(PrivateFiles.OwnerFile, File.GetUnixFileMode(Path.Combine(logs, "xaultwallet.log")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup only */ }
    }
}
