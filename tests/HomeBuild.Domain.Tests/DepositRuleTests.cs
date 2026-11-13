using static HomeBuild.Testing.Builders;

namespace HomeBuild.Domain.Tests;

/// <summary>
/// The deposit rule (Unit.Reserve): minimum deposit ≤ deposit ≤ price, in the unit's currency, on an available unit.
/// Boundaries for a unit priced 250,000.00 with a minimum deposit of 1,000.00.
/// </summary>
public class DepositRuleTests
{
    private const decimal Price = 250_000m;
    private const decimal Minimum = 1_000m;

    public static TheoryData<decimal, bool> Deposits => new()
    {
        new TheoryDataRow<decimal, bool>(0m, false).WithTestDisplayName("Deposit: zero is rejected"),
        new TheoryDataRow<decimal, bool>(Minimum - 0.01m, false).WithTestDisplayName("Deposit: the minimum minus one penny is rejected"),
        new TheoryDataRow<decimal, bool>(Minimum, true).WithTestDisplayName("Deposit: exactly the minimum is accepted"),
        new TheoryDataRow<decimal, bool>(Price, true).WithTestDisplayName("Deposit: exactly the price is accepted"),
        new TheoryDataRow<decimal, bool>(Price + 0.01m, false).WithTestDisplayName("Deposit: the price plus one penny is rejected"),
    };

    [Theory]
    [MemberData(nameof(Deposits))]
    public void Deposit_must_be_between_the_minimum_and_the_price(decimal deposit, bool accepted)
    {
        var unit = AUnit().PricedAt(Amount(Price)).Build();
        var policy = APolicy().WithMinimumDeposit(Amount(Minimum)).Build();

        var result = unit.Reserve(BuyerId.New(), Amount(deposit), policy, Now).Value;

        if (accepted)
        {
            Assert.IsType<Reserved>(result);
            Assert.Equal(UnitStatus.Reserved, unit.Status);
        }
        else
        {
            var rejected = Assert.IsType<DepositRejected>(result);
            Assert.Equal(Amount(deposit), rejected.Offered);
            Assert.Equal(UnitStatus.Available, unit.Status);
        }
    }

    [Fact]
    public void Deposit_in_a_currency_other_than_the_units_is_refused()
    {
        var unit = AUnit().PricedAt(new Money(Price, "GBP")).Build();

        Assert.Throws<ArgumentException>(() => unit.Reserve(BuyerId.New(), new Money(5_000m, "EUR"), APolicy().Build(), Now));
        Assert.Equal(UnitStatus.Available, unit.Status);
    }

    [Fact]
    public void Valid_deposit_on_a_reserved_unit_is_unavailable_not_rejected()
    {
        var unit = AUnit().Reserved().Build();

        Assert.IsType<UnitUnavailable>(unit.Reserve(BuyerId.New(), Amount(5_000m), APolicy().Build(), Now).Value);
    }
}
