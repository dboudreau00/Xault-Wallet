using System.Text.RegularExpressions;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>The real and the decoy seed backup are saved under the same naming scheme.</summary>
public class SeedBackupFileTests
{
    [Fact]
    public void Suggested_Name_Never_Mentions_A_Decoy_Or_Duress()
    {
        for (int i = 0; i < 100; i++)
        {
            string name = SeedBackupFile.SuggestedName(DateTime.Now);
            Assert.DoesNotContain("decoy", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("duress", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("real", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Suggested_Name_Has_The_Neutral_Shape()
    {
        string name = SeedBackupFile.SuggestedName(new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Local));
        Assert.Matches(new Regex("^wallet-backup-20261006-[0-9]{6}\\.txt$"), name);
    }

    [Fact]
    public void Two_Backups_On_The_Same_Day_Usually_Get_Different_Names()
    {
        var day = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Local);
        var names = new HashSet<string>(Enumerable.Range(0, 20).Select(_ => SeedBackupFile.SuggestedName(day)));
        Assert.True(names.Count > 1);
    }
}
