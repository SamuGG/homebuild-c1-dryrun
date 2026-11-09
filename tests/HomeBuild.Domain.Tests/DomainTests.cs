using HomeBuild.Domain;

namespace HomeBuild.Domain.Tests;

public class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hold = TimeSpan.FromDays(7);
    private static readonly Money MinDeposit = new(1000m, "GBP");
    private static readonly ReservationPolicy Policy = new(Hold, MinDeposit);

    private static Unit NewUnit() => new(UnitId.New(), DevelopmentId.New(), "P-01", new Money(250_000m, "GBP"));

    [Fact]
    public void Money_rejects_negative_amount() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(-1m, "GBP"));

    [Fact]
    public void Money_values_with_same_amount_and_currency_are_equal() =>
        Assert.Equal(new Money(10m, "GBP"), new Money(10m, "GBP"));

    [Fact]
    public void Adding_money_in_different_currencies_throws() =>
        Assert.Throws<InvalidOperationException>(() => new Money(1m, "GBP") + new Money(1m, "EUR"));

    [Fact]
    public void Unit_rejects_zero_price() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Unit(UnitId.New(), DevelopmentId.New(), "P-01", new Money(0m, "GBP")));

    [Fact]
    public void Reserving_available_unit_creates_reservation_and_marks_unit_reserved()
    {
        var unit = NewUnit();
        var buyer = BuyerId.New();
        var reservation = Assert.IsType<Reserved>(unit.Reserve(buyer, new Money(5000m, "GBP"), Policy, Now).Value).Reservation;
        Assert.Equal(unit.Id, reservation.UnitId);
        Assert.Equal(buyer, reservation.BuyerId);
        Assert.Equal(UnitStatus.Reserved, unit.Status);
    }

    [Fact]
    public void Reserving_reserved_unit_fails()
    {
        var unit = NewUnit();
        unit.Reserve(BuyerId.New(), new Money(5000m, "GBP"), Policy, Now);
        Assert.IsType<UnitUnavailable>(unit.Reserve(BuyerId.New(), new Money(5000m, "GBP"), Policy, Now).Value);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(250_001)]
    public void Deposit_below_minimum_or_above_price_is_rejected(decimal amount) =>
        Assert.IsType<DepositRejected>(NewUnit().Reserve(BuyerId.New(), new Money(amount, "GBP"), Policy, Now).Value);

    [Fact]
    public void Deposit_in_other_currency_is_rejected() =>
        Assert.Throws<ArgumentException>(() =>
            NewUnit().Reserve(BuyerId.New(), new Money(5000m, "EUR"), Policy, Now));

    [Fact]
    public void Reservation_expires_after_hold_period()
    {
        var reservation = Assert.IsType<Reserved>(NewUnit().Reserve(BuyerId.New(), new Money(5000m, "GBP"), Policy, Now).Value).Reservation;
        Assert.Equal(Now + Hold, reservation.ExpiresAt);
        Assert.False(reservation.IsExpired(Now + Hold - TimeSpan.FromSeconds(1)));
        Assert.True(reservation.IsExpired(Now + Hold));
    }
}
