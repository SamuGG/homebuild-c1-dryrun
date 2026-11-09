namespace HomeBuild.Domain.Tests;

public class ResultTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hold = TimeSpan.FromDays(7);
    private static readonly Money MinDeposit = new(1000m, "GBP");
    private static readonly ReservationPolicy Policy = new(Hold, MinDeposit);

    private static Unit NewUnit() => new(UnitId.New(), DevelopmentId.New(), "P-01", new Money(250_000m, "GBP"));

    private static ReserveResult Reserve(Unit unit, decimal deposit = 5000m) =>
        unit.Reserve(BuyerId.New(), new Money(deposit, "GBP"), Policy, Now);

    private static string Describe(ReserveResult result) => result switch
    {
        Reserved r => $"reserved {r.Reservation.Id}",
        UnitUnavailable u => $"unavailable {u.Status}",
        DepositRejected d => $"rejected {d.Offered.Amount}",
    };

    [Fact]
    public void Reserving_available_unit_returns_Reserved() =>
        Assert.StartsWith("reserved", Describe(Reserve(NewUnit())), StringComparison.Ordinal);

    [Fact]
    public void Reserving_reserved_unit_returns_UnitUnavailable()
    {
        var unit = NewUnit();
        Reserve(unit);
        Assert.Equal("unavailable Reserved", Describe(Reserve(unit)));
    }

    [Fact]
    public void Deposit_below_minimum_returns_DepositRejected() =>
        Assert.Equal("rejected 999", Describe(Reserve(NewUnit(), 999m)));

    [Fact]
    public void Held_reservation_past_expiry_becomes_Expired()
    {
        var reservation = Assert.IsType<Reserved>(Reserve(NewUnit()).Value).Reservation;
        Assert.IsType<Transitioned>(reservation.Expire(Now + Hold).Value);
        Assert.IsType<Expired>(reservation.State);
    }

    [Fact]
    public void Expired_reservation_cannot_be_confirmed()
    {
        var reservation = Assert.IsType<Reserved>(Reserve(NewUnit()).Value).Reservation;
        reservation.Expire(Now + Hold);
        Assert.IsType<InvalidTransition>(reservation.Confirm(Now + Hold).Value);
        Assert.IsType<Expired>(reservation.State);
    }

    [Fact]
    public void Adding_money_in_different_currencies_still_throws() =>
        Assert.Throws<InvalidOperationException>(() => new Money(1m, "GBP") + new Money(1m, "EUR"));
}
