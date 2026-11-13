using static HomeBuild.Testing.Builders;
namespace HomeBuild.Domain.Tests;

public class ResultTests
{
    private static readonly ReservationPolicy Policy = APolicy().Build();
    private static readonly TimeSpan Hold = Policy.HoldPeriod;

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
        Assert.StartsWith("reserved", Describe(Reserve(AUnit().Build())), StringComparison.Ordinal);

    [Fact]
    public void Reserving_reserved_unit_returns_UnitUnavailable()
    {
        var unit = AUnit().Build();
        Reserve(unit);
        Assert.Equal("unavailable Reserved", Describe(Reserve(unit)));
    }

    [Fact]
    public void Deposit_below_minimum_returns_DepositRejected() =>
        Assert.Equal("rejected 999", Describe(Reserve(AUnit().Build(), 999m)));

    [Fact]
    public void Held_reservation_past_expiry_becomes_Expired()
    {
        var reservation = Assert.IsType<Reserved>(Reserve(AUnit().Build()).Value).Reservation;
        Assert.IsType<Transitioned>(reservation.Expire(Now + Hold).Value);
        Assert.IsType<Expired>(reservation.State);
    }

    [Fact]
    public void Expired_reservation_cannot_be_confirmed()
    {
        var reservation = Assert.IsType<Reserved>(Reserve(AUnit().Build()).Value).Reservation;
        reservation.Expire(Now + Hold);
        Assert.IsType<InvalidTransition>(reservation.Confirm(Now + Hold).Value);
        Assert.IsType<Expired>(reservation.State);
    }

    [Fact]
    public void Adding_money_in_different_currencies_still_throws() =>
        Assert.Throws<InvalidOperationException>(() => new Money(1m, "GBP") + new Money(1m, "EUR"));
}
