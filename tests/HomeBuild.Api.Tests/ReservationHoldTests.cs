using static HomeBuild.Testing.Builders;

namespace HomeBuild.Api.Tests;

/// <summary>
/// Formerly the PLANTED FLAKY TEST (C6 Lab 5). It failed in about half the runs: the second test read a reservation the
/// first left in a static field (test order), and compared the wall clock twice a few milliseconds apart (time). Fixed by
/// making each test build its own data on the builders' fixed clock and test the boundary exactly. No retries.
/// </summary>
public sealed class ReservationHoldTests
{
    [Fact]
    public void New_reservation_is_held_for_seven_days()
    {
        var reservation = AReservation().CreatedAt(Now).Build();

        Assert.Equal(TimeSpan.FromDays(7), reservation.ExpiresAt - reservation.CreatedAt);
    }

    [Fact]
    public void Reservation_is_still_held_just_before_its_hold_ends()
    {
        var reservation = AReservation().CreatedAt(Now).Build();

        Assert.False(reservation.IsExpired(reservation.ExpiresAt - TimeSpan.FromTicks(1)));
        Assert.True(reservation.IsExpired(reservation.ExpiresAt));
    }
}
