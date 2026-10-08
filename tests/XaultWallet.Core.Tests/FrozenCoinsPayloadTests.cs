using System.Text;
using XaultWallet.Core.Models;
using XaultWallet.Core.Security;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>
/// Frozen coins live in the vault (the wallet file is shredded on lock), in a field every slot has
/// whatever it holds, so it says nothing about which password is the real one.
/// </summary>
public sealed class FrozenCoinsPayloadTests
{
    private static readonly string KeyImageA = new('a', 64);
    private static readonly string KeyImageB = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Frozen_Coins_Round_Trip()
    {
        var wallet = new WalletSecrets { Mnemonic = "words", FrozenKeyImages = { KeyImageA, KeyImageB } };

        WalletProfile back = SlotPayload.Deserialize(SlotPayload.Serialize(WalletProfile.OfOne(wallet)), out int version);

        Assert.Equal(SlotPayload.CurrentVersion, version);
        Assert.Equal(new[] { KeyImageA, KeyImageB }, back.Wallets[0].FrozenKeyImages);
    }

    [Fact]
    public void Every_Slot_Carries_The_Field_Even_When_Nothing_Is_Frozen()
    {
        string json = Encoding.UTF8.GetString(SlotPayload.Serialize(WalletProfile.OfOne(new WalletSecrets { Mnemonic = "w" })));
        Assert.Contains("\"frozen\":[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Vault_From_Before_Coin_Control_Opens_With_Nothing_Frozen()
    {
        byte[] old = Encoding.UTF8.GetBytes("""{"v":3,"wipeOther":false,"active":"x","wallets":[{"id":"x","name":"W","type":0,"network":1,"mnemonic":"m","notes":{}}],"contacts":[]}""");

        WalletProfile p = SlotPayload.Deserialize(old, out _);

        Assert.Empty(p.Wallets[0].FrozenKeyImages);
    }

    [Fact]
    public void Anything_That_Is_Not_A_Key_Image_Is_Dropped_Not_Sent_To_The_Backend()
    {
        string junk = "{\"v\":3,\"active\":\"x\",\"wallets\":[{\"id\":\"x\",\"type\":0,\"network\":1,\"mnemonic\":\"m\",\"frozen\":[" +
                      $"\"{KeyImageA}\",\"{KeyImageA}\",\"{KeyImageA.ToUpperInvariant()}\",\"short\",\"{new string('g', 64)}\",\"--freeze\",\"{KeyImageB}\"" +
                      "]}],\"contacts\":[]}";

        WalletProfile p = SlotPayload.Deserialize(Encoding.UTF8.GetBytes(junk), out _);

        Assert.Equal(new[] { KeyImageA, KeyImageB }, p.Wallets[0].FrozenKeyImages);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Key_Image_Shape(string? value, bool ok) => Assert.Equal(ok, SlotPayload.IsKeyImage(value));
}
