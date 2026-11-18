using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using HomeBuild.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Stripe;
using Stripe.Checkout;

namespace HomeBuild.Api.Modules.Payments.StripeWebhook;

public enum WebhookResult
{
    /// <summary>Not an event HomeBuild handles, or not a HomeBuild deposit session: acknowledged, nothing stored.</summary>
    Ignored,

    /// <summary>Already processed (a redelivery, a resend, or a concurrent duplicate): acknowledged, nothing changed.</summary>
    Duplicate,

    DepositConfirmed,

    /// <summary>Paid, but the reservation was already confirmed: the deposit is recorded, the reservation unchanged.</summary>
    DepositAlreadyConfirmed,

    /// <summary>Paid, but the reservation is expired or cancelled: recorded for refund or re-hold (Lab 5).</summary>
    DepositLate,

    SessionExpired,
}

/// <summary>
/// C8 Lab 2: what a verified Stripe event does. Everything happens in one short transaction: the event ID goes into
/// processed_webhook_events first (ON CONFLICT DO NOTHING: a duplicate, even a concurrent one, finds it there and stops),
/// then the deposit is recorded and Sales confirms the reservation on the same transaction. Commit, then acknowledge.
/// A crash anywhere before the commit leaves nothing behind, so Stripe's retry processes the event once.
/// </summary>
internal sealed class StripeDepositWebhook(PaymentsDbContext db, IReservationDeposits reservations, RequestTenantContext tenant, TimeProvider time)
{
    public async Task<(WebhookResult Result, ReservationId? Reservation)> HandleAsync(Event stripeEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stripeEvent);
        if (stripeEvent.Type is not (EventTypes.CheckoutSessionCompleted or EventTypes.CheckoutSessionExpired)
            || stripeEvent.Data.Object is not Session session
            || !Guid.TryParse(session.ClientReferenceId, out var reservationGuid)
            || session.Metadata?.GetValueOrDefault(StripePaymentGateway.TenantMetadata) is not { } tenantText
            || !Guid.TryParse(tenantText, out var tenantId))
        {
            return (WebhookResult.Ignored, null);
        }

        // The tenant comes from the session's metadata, which HomeBuild wrote and Stripe signed; the request has no user.
        tenant.TenantId = tenantId;
        var reservationId = new ReservationId(reservationGuid);

        // C8 Lab 4: the whole unit of work runs under the execution strategy, so a transient failure (even at commit)
        // replays all of it from the start: the event ID insert, the deposit, the confirmation.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            (stripeEvent, session, reservationId, tenantId),
            async (_, state, ct) => await ProcessAsync(state.stripeEvent, state.session, state.reservationId, state.tenantId, ct),
            verifySucceeded: null,
            cancellationToken);
    }

    private async Task<(WebhookResult Result, ReservationId? Reservation)> ProcessAsync(Event stripeEvent, Session session, ReservationId reservationId, Guid tenantId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear(); // a retried attempt starts from nothing
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var first = await db.Database.ExecuteSqlAsync(
            $"INSERT INTO payments.processed_webhook_events (event_id, type, processed_at, tenant_id) VALUES ({stripeEvent.Id}, {stripeEvent.Type}, {time.GetUtcNow()}, {tenantId}) ON CONFLICT (event_id) DO NOTHING",
            cancellationToken);
        if (first == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (WebhookResult.Duplicate, reservationId);
        }

        var result = stripeEvent.Type == EventTypes.CheckoutSessionCompleted
            ? await CompletedAsync(session, reservationId, new DateTimeOffset(DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc)), transaction, cancellationToken)
            : await ExpiredAsync(session, reservationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (result, reservationId);
    }

    private async Task<WebhookResult> CompletedAsync(Session session, ReservationId reservationId, DateTimeOffset paidAt, IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        var paid = new DepositPaid(reservationId, StripeAmounts.FromMinorUnits(session.AmountTotal ?? 0, session.Currency), session.PaymentIntentId ?? session.Id, paidAt);
        db.Deposits.Add(new DepositRow
        {
            ReservationId = paid.ReservationId, SessionId = session.Id, ProviderReference = paid.ProviderReference,
            Amount = paid.Amount.Amount, Currency = paid.Amount.Currency, PaidAt = paid.PaidAt,
        });
        await db.SaveChangesAsync(cancellationToken);

        return (await reservations.ConfirmPaidAsync(reservationId, paidAt, transaction.GetDbTransaction(), cancellationToken)).Outcome switch
        {
            DepositConfirmation.Confirmed => WebhookResult.DepositConfirmed,
            DepositConfirmation.AlreadyConfirmed => WebhookResult.DepositAlreadyConfirmed,
            _ => WebhookResult.DepositLate,
        };
    }

    private async Task<WebhookResult> ExpiredAsync(Session session, ReservationId reservationId, CancellationToken cancellationToken)
    {
        // Payment did not happen on this page. The reservation is not touched: its hold ends on its own clock, and a
        // completed event for another session (or arriving later) still confirms it.
        db.ExpiredCheckoutSessions.Add(new ExpiredCheckoutSession { SessionId = session.Id, ReservationId = reservationId, ExpiredAt = time.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return WebhookResult.SessionExpired;
    }
}
