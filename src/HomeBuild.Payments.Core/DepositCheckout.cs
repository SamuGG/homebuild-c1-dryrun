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
                "HomeBuild reservation deposit", command.SuccessUrl, command.CancelUrl, closesAt),
            cancellationToken);
        return new DepositCheckoutStarted(reservation.Id, session);
    }
}

/// <summary>
/// When the payment page closes. The brief says "with the reservation", but Stripe accepts expires_at only from 30
/// minutes to 24 hours after the session is created, and the hold is configurable from 1 hour to 14 days (7 days by
/// default). So the page closes when the hold ends if that is within 24 hours, and after 24 hours otherwise (the buyer
/// asks for a new page): it never outlives the reservation.
/// </summary>
internal static class CheckoutWindow
{
    public static readonly TimeSpan Shortest = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    // Stripe measures from when it creates the session, a little after "now": a minute's margin at each end keeps a
    // slow request inside the window.
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    /// <summary>The page's expiry, or null if the hold ends too soon for the provider to open one.</summary>
    public static DateTimeOffset? ClosesAt(DateTimeOffset now, DateTimeOffset holdEndsAt)
    {
        if (holdEndsAt - now < Shortest + Margin)
        {
            return null;
        }

        var latest = now + Longest - Margin;
        return holdEndsAt < latest ? holdEndsAt : latest;
    }
}
