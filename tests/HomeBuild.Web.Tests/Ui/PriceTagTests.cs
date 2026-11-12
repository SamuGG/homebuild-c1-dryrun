using Bunit;
using HomeBuild.Web.Components.Ui;

namespace HomeBuild.Web.Tests.Ui;

public sealed class PriceTagTests : UiTestContext
{
    [Theory]
    [InlineData(260000, "GBP", "£260,000")]
    [InlineData(199500.50, "EUR", "199,501 EUR")]
    public void Renders_money_with_a_machine_readable_value(decimal amount, string currency, string text)
    {
        var cut = Render<PriceTag>(p => p.Add(t => t.Amount, amount).Add(t => t.Currency, currency));

        var data = cut.Find("data");
        Assert.Equal(text, data.TextContent);
        Assert.Equal(amount.ToString(System.Globalization.CultureInfo.InvariantCulture), data.GetAttribute("value"));
    }
}
