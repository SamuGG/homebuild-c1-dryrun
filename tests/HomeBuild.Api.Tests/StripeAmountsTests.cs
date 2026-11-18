using HomeBuild.Api.Modules.Payments;
using HomeBuild.Domain;

namespace HomeBuild.Api.Tests;

/// <summary>C8 Lab 1: Money to Stripe's minor units, with the boundary rows (C6) for each kind of currency.</summary>
public sealed class StripeAmountsTests
{
    [Theory]
    [InlineData("5000", "GBP", 500_000)] // a deposit in pounds is sent in pence
    [InlineData("0.01", "GBP", 1)] // smallest two-decimal amount
    [InlineData("1999.99", "GBP", 199_999)]
    [InlineData("9999999999.99", "GBP", 999_999_999_999)] // the 12-digit card maximum
    [InlineData("500", "JPY", 500)] // zero-decimal: not multiplied
    [InlineData("1", "KRW", 1)]
    [InlineData("5", "ISK", 500)] // zero-decimal sent as two-decimal, decimals always 00
    [InlineData("5", "UGX", 500)]
    [InlineData("1.23", "KWD", 1_230)] // three-decimal: last digit 0
    [InlineData("10.45", "HUF", 1_045)] // two-decimal for charges (only payouts are whole)
    public void Converts_to_minor_units(string amount, string currency, long expected) =>
        Assert.Equal(expected, StripeAmounts.ToMinorUnits(new Money(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency)));

    [Theory]
    [InlineData("0.001", "GBP")] // a fraction of a penny: refused, not rounded
    [InlineData("5000.005", "GBP")]
    [InlineData("500.5", "JPY")] // fractions of a zero-decimal currency
    [InlineData("5.5", "ISK")]
    [InlineData("1.234", "KWD")] // Stripe needs the third decimal to be 0
    [InlineData("10000000000", "GBP")] // 13 digits in pence
    public void Refuses_amounts_Stripe_cannot_charge_exactly(string amount, string currency) =>
        Assert.Throws<ArgumentException>(() => StripeAmounts.ToMinorUnits(new Money(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency)));

    [Fact]
    public void Currency_is_lowercase() => Assert.Equal("gbp", StripeAmounts.Currency(new Money(1m, "GBP")));
}
