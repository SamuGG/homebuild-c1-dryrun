using HomeBuild.Domain;
using static HomeBuild.Testing.Builders;

namespace HomeBuild.Payments.Tests;

/// <summary>C8 Lab 1: the deposit checkout rules, with the fake gateway (the Stripe adapter is tested in the API).</summary>
public sealed class StartDepositCheckoutTests
{
    private static readonly Guid Tenant = Guid.Parse("0198a000-0000-7000-8000-00000000000a");

    [Fact]
    public async Task Held_reservation_gets_a_session_for_its_deposit_with_its_ids()
    {
        var reservation = AReservation().CreatedAt(Now).HeldFor(TimeSpan.FromHours(2)).Build();
        var (checkout, gateway) = Create(Now);

        var result = await checkout.HandleAsync(Command(reservation), TestContext.Current.CancellationToken);

        Assert.IsType<DepositCheckoutStarted>(result.Value);
        var request = Assert.Single(gateway.Checkouts);
        Assert.Equal((reservation.Id, Tenant, reservation.UnitId, reservation.Deposit), (request.ReservationId, request.TenantId, request.UnitId, request.Amount));
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    public async Task Only_a_held_reservation_gets_a_session(string state)
    {
        var builder = AReservation().CreatedAt(Now);
        var reservation = (state switch { "confirmed" => builder.Confirmed(), "cancelled" => builder.Cancelled(), _ => builder.Expired() }).Build();
        var (checkout, gateway) = Create(Now);

        var result = await checkout.HandleAsync(Command(reservation), TestContext.Current.CancellationToken);

        Assert.IsType<ReservationNotHeld>(result.Value);
        Assert.Empty(gateway.Checkouts);
    }

    // The window's boundary rows (C6): Stripe's shortest page is 30 minutes and its longest 24 hours, each with a minute's margin.
    [Theory]
    [InlineData(30, null)] // the hold ends in 30 minutes: too soon
    [InlineData(31, 31)] // first minute that fits: the page closes with the hold
    [InlineData(60, 60)] // the C1 minimum hold of 1 hour
    [InlineData(23 * 60 + 59, 23 * 60 + 59)] // last minute that still closes with the hold
    [InlineData(24 * 60, 23 * 60 + 59)] // a 24-hour hold: capped just inside Stripe's maximum
    [InlineData(7 * 24 * 60, 23 * 60 + 59)] // the configured 7-day hold: capped, never after the hold ends
    public async Task Session_closes_with_the_hold_inside_Stripes_window(int holdEndsInMinutes, int? closesInMinutes)
    {
        var reservation = AReservation().CreatedAt(Now).HeldFor(TimeSpan.FromMinutes(holdEndsInMinutes)).Build();
        var (checkout, gateway) = Create(Now);

        var result = await checkout.HandleAsync(Command(reservation), TestContext.Current.CancellationToken);

        if (closesInMinutes is null)
        {
            Assert.IsType<HoldEndsTooSoon>(result.Value);
            Assert.Empty(gateway.Checkouts);
        }
        else
        {
            Assert.Equal(Now.AddMinutes(closesInMinutes.Value), Assert.Single(gateway.Checkouts).ExpiresAt);
            Assert.True(gateway.Checkouts[0].ExpiresAt <= reservation.ExpiresAt);
        }
    }

    private static StartDepositCheckoutCommand Command(Reservation reservation) =>
        new(reservation, Tenant, new Uri("https://web.example/my-home?deposit=paid"), new Uri("https://web.example/my-home?deposit=cancelled"));

    private static (StartDepositCheckout Checkout, FakeGateway Gateway) Create(DateTimeOffset now)
    {
        var gateway = new FakeGateway();
        return (new StartDepositCheckout(gateway, new FixedTime(now)), gateway);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
