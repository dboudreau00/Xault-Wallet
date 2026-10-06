using System.Globalization;
using System.Security.Cryptography;

namespace XaultWallet.Core.Security;

/// <summary>
/// The file name the save dialog suggests for a plaintext seed backup. ONE scheme for every
/// backup — the real wallet's and the decoy's alike — so a name left on disk (or in a recent-files
/// list) never says which is which, or that a decoy exists at all.
/// </summary>
public static class SeedBackupFile
{
    /// <summary>
    /// <c>wallet-backup-&lt;yyyyMMdd&gt;-&lt;6 random digits&gt;.txt</c>. The random part keeps two backups
    /// saved on the same day (real and decoy) from suggesting the same name.
    /// </summary>
    public static string SuggestedName(DateTime now) =>
        "wallet-backup-" +
        now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" +
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture) +
        ".txt";
}
