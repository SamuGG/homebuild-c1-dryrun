using HomeBuild.Domain;

namespace HomeBuild.Payments;

/// <summary>Command: open a payment page for a held reservation's deposit (C8 Lab 1).</summary>
public sealed record StartDepositCheckoutCommand(Reservation Reservation, Guid TenantId, Uri SuccessUrl, Uri CancelUrl);

public sealed record DepositCheckoutStarted(ReservationId ReservationId, CheckoutSession Session);

/// <summary>Only a held reservation takes a deposit: confirmed, expired and cancelled ones do not.</summary>
public sealed record ReservationNotHeld(ReservationId ReservationId, string State);

/// <summary>The hold ends sooner than the provider's shortest payment page (30 minutes): too late to pay online.</summary>
public sealed record HoldEndsTooSoon(ReservationId ReservationId, DateTimeOffset ExpiresAt);

public union StartDepositCheckoutResult(DepositCheckoutStarted, ReservationNotHeld, HoldEndsTooSoon);

/// <summary>
/// Use case: a Checkout Session per held reservation, for the deposit, closing when the reservation's hold ends. The
/// caller has already checked that the buyer owns the reservation.
/// </summary>
public sealed class StartDepositCheckout(IPaymentGateway gateway, TimeProvider time)
{
    public async Task<StartDepositCheckoutResult> HandleAsync(StartDepositCheckoutCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reservation = command.Reservation;
        if (reservation.State is not Held held)
        {
            return new ReservationNotHeld(reservation.Id, reservation.State.GetType().Name);
        }

        if (CheckoutWindow.ClosesAt(time.GetUtcNow(), held.ExpiresAt) is not { } closesAt)
        {
            return new HoldEndsTooSoon(reservation.Id, held.ExpiresAt);
        }

        var session = await gateway.CreateCheckoutSessionAsync(
            new CheckoutRequest(reservation.Id, command.TenantId, reservation.UnitId, reservation.Deposit,
                "HomeBuild reservation deposit", command.SuccessUrl, command.CancelUrl, closesAt, CheckoutWindow.IdempotencyKey(reservation.Id, closesAt)),
            cancellationToken);
        return new DepositCheckoutStarted(reservation.Id, session);
    }
}

/// <summary>
/// When the payment page closes. The brief says "with the reservation", but Stripe accepts expires_at only from 30
/// minutes to 24 hours after the session is created, and the hold is configurable from 1 hour to 14 days (7 days by
/// default). So the page closes when the hold ends if that is soon enough, and otherwise 23 hours after the start of
/// the current hour (the buyer asks for a new page later): it never outlives the reservation.
/// <para>
/// C8 Lab 3: the cap is on an hourly grid, not "now + 24 h", because Stripe refuses an idempotency key reused with
/// different parameters (idempotency_error). Within one hour, every retry for a reservation sends the same expires_at
/// and so the same key, and gets the same session.
/// </para>
/// </summary>
internal static class CheckoutWindow
{
    public static readonly TimeSpan Shortest = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    /// <summary>The cap: 23 hours after the start of the hour, so between 22 and 23 hours away, inside Stripe's 24.</summary>
    private static readonly TimeSpan Cap = TimeSpan.FromHours(23);

    // Stripe measures from when it creates the session, a little after "now": a minute's margin at each end keeps a
    // slow request inside the window.
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    /// <summary>
    /// C8 Lab 3: the key HomeBuild derives for Stripe, so a retried create does not open a second page. The brief's
    /// deposit-session:{reservationId} alone fails once the cap moves (see the class summary), so the expiry is part of it.
    /// </summary>
    public static string IdempotencyKey(ReservationId reservationId, DateTimeOffset closesAt) =>
        $"deposit-session:{reservationId.Value}:{closesAt.ToUnixTimeSeconds()}";

    /// <summary>The page's expiry, or null if the hold ends too soon for the provider to open one.</summary>
    public static DateTimeOffset? ClosesAt(DateTimeOffset now, DateTimeOffset holdEndsAt)
    {
        if (holdEndsAt - now < Shortest + Margin)
        {
            return null;
        }

        var hour = new DateTimeOffset(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour), TimeSpan.Zero);
        var latest = hour + Cap;
        return holdEndsAt < latest ? holdEndsAt : latest;
    }
}
