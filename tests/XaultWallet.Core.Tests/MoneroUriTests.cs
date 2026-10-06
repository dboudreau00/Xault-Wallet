using XaultWallet.Core.Monero;
using Xunit;

namespace XaultWallet.Core.Tests;

/// <summary>monero: payment links, as Receive builds them and Send reads them.</summary>
public class MoneroUriTests
{
    private const string Address = "44AFFq5kSiGBoZ4NMDwYtN18obc8AemS33DBLWs3H7otXft3XjrpDtQGv7SqSsaBYBb98uNbr2VBBEt7f2wfn3RVGQBEP3A";

    [Fact]
    public void Plain_Address_Round_Trips()
    {
        string uri = MoneroUri.Build(new MoneroPaymentRequest(Address));
        Assert.Equal("monero:" + Address, uri);
        Assert.Equal(new MoneroPaymentRequest(Address), MoneroUri.TryParse(uri, out _));
    }

    [Fact]
    public void Amount_Description_And_Name_Round_Trip_Escaped()
    {
        var request = new MoneroPaymentRequest(Address, 1.25m, "Rent & utilities, July", "Alice Ö");
        string uri = MoneroUri.Build(request);

        Assert.Equal($"monero:{Address}?tx_amount=1.25&tx_description=Rent%20%26%20utilities%2C%20July&recipient_name=Alice%20%C3%96", uri);
        Assert.Equal(request, MoneroUri.TryParse(uri, out string? problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("monero:{0}?tx_amount=0.000000000001", "0.000000000001")]
    [InlineData("MONERO:{0}?TX_AMOUNT=12", "12")]
    [InlineData("monero://{0}?tx_amount=3.5&unknown=x", "3.5")]
    [InlineData("  monero:{0}?tx_description=a+b&tx_amount=2  ", "2")]
    public void Lenient_Where_It_Is_Harmless(string template, string amount)
    {
        MoneroPaymentRequest? r = MoneroUri.TryParse(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Address), out string? problem);
        Assert.Null(problem);
        Assert.Equal(Address, r!.Address);
        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), r.Amount);
    }

    [Theory]
    [InlineData("bitcoin:1abc")]
    [InlineData(Address)]
    [InlineData("monero:")]
    [InlineData("monero:?tx_amount=1")]
    [InlineData("monero:" + Address + "?tx_amount=abc")]
    [InlineData("monero:" + Address + "?tx_amount=-1")]
    [InlineData("monero:" + Address + "?tx_amount=0")]
    [InlineData("monero:" + Address + "?tx_amount=1,5")]
    [InlineData("monero:" + Address + "?tx_payment_id=0123456789abcdef")]
    [InlineData("monero:" + Address + ";" + Address + "?tx_amount=1;2")]
    public void Refuses_What_It_Cannot_Pay_Exactly(string uri)
    {
        Assert.Null(MoneroUri.TryParse(uri, out string? problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void Detects_A_Pasted_Link()
    {
        Assert.True(MoneroUri.IsUri(" monero:" + Address));
        Assert.True(MoneroUri.IsUri("Monero:" + Address));
        Assert.False(MoneroUri.IsUri(Address));
        Assert.False(MoneroUri.IsUri(null));
    }

    [Fact]
    public void Zero_Or_Missing_Amount_Is_Left_Out()
    {
        Assert.Equal("monero:" + Address, MoneroUri.Build(new MoneroPaymentRequest(Address, 0m, "  ")));
    }
}
