using static HomeBuild.Testing.Builders;

namespace HomeBuild.Domain.Tests;

/// <summary>Money's rules as boundary tables: amounts, currency codes, addition and equality (docs/testing.md).</summary>
public class MoneyRuleTests
{
    public static TheoryData<decimal, bool> Amounts => new()
    {
        new TheoryDataRow<decimal, bool>(0m, true).WithTestDisplayName("Money amount: zero is allowed"),
        new TheoryDataRow<decimal, bool>(0.01m, true).WithTestDisplayName("Money amount: one penny is allowed"),
        new TheoryDataRow<decimal, bool>(-0.01m, false).WithTestDisplayName("Money amount: one penny below zero is rejected"),
        new TheoryDataRow<decimal, bool>(100_000_000m, true).WithTestDisplayName("Money amount: a large price is allowed"),
    };

    public static TheoryData<string, bool> Currencies => new()
    {
        new TheoryDataRow<string, bool>("GBP", true).WithTestDisplayName("Currency: three upper-case letters are allowed"),
        new TheoryDataRow<string, bool>("gbp", false).WithTestDisplayName("Currency: lower case is rejected"),
        new TheoryDataRow<string, bool>("GB", false).WithTestDisplayName("Currency: two letters are rejected"),
        new TheoryDataRow<string, bool>("GBPX", false).WithTestDisplayName("Currency: four letters are rejected"),
        new TheoryDataRow<string, bool>("G1P", false).WithTestDisplayName("Currency: a digit is rejected"),
        new TheoryDataRow<string, bool>("", false).WithTestDisplayName("Currency: empty is rejected"),
    };

    [Theory]
    [MemberData(nameof(Amounts))]
    public void Money_accepts_only_non_negative_amounts(decimal amount, bool valid)
    {
        var create = () => new Money(amount, DefaultCurrency);

        if (valid)
        {
            Assert.Equal(amount, create().Amount);
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(create);
        }
    }

    [Theory]
    [MemberData(nameof(Currencies))]
    public void Money_accepts_only_three_letter_upper_case_currency_codes(string currency, bool valid)
    {
        var create = () => new Money(1m, currency);

        if (valid)
        {
            Assert.Equal(currency, create().Currency);
        }
        else
        {
            Assert.Throws<ArgumentException>(create);
        }
    }

    [Fact]
    public void Money_in_the_same_currency_adds_up() =>
        Assert.Equal(new Money(3.50m, "GBP"), new Money(1.25m, "GBP") + new Money(2.25m, "GBP"));

    [Fact]
    public void Money_in_mixed_currencies_cannot_be_added() =>
        Assert.Throws<InvalidOperationException>(() => new Money(1m, "GBP") + new Money(1m, "EUR"));

    [Fact]
    public void Money_with_the_same_amount_in_another_currency_is_not_equal() =>
        Assert.NotEqual(new Money(10m, "GBP"), new Money(10m, "EUR"));
}
