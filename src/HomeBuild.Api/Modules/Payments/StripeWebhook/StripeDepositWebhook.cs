using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using HomeBuild.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Stripe;
using Stripe.Checkout;
using HomeBuild.Messaging;

namespace HomeBuild.Api.Modules.Payments.StripeWebhook;

public enum WebhookResult
{
    /// <summary>Not an event HomeBuild handles, or not a HomeBuild deposit session: acknowledged, nothing stored.</summary>
    Ignored,

    /// <summary>Already processed (a redelivery, a resend, or a concurrent duplicate): acknowledged, nothing changed.</summary>
    Duplicate,

    DepositConfirmed,

    /// <summary>Paid twice for one reservation by an earlier session (a duplicate event is <see cref="Duplicate"/>).</summary>
    DepositAlreadyConfirmed,

    /// <summary>C8 Lab 5: paid after the hold ended, unit still free: held again and confirmed.</summary>
    DepositReheldAndConfirmed,

    /// <summary>C8 Lab 5: the payment cannot be kept (unit taken, cancelled, paid twice): refunded after the commit.</summary>
    DepositRefunded,

    SessionExpired,

    /// <summary>
    /// C8 Lab 6: an event already processed, delivered again, whose deposit is missing (found by reconciliation): the
    /// deposit is recorded again. The deposit's unique session key keeps it to one row.
    /// </summary>
    DepositRepaired,
}

/// <summary>
/// C8 Lab 2: what a verified Stripe event does. Everything happens in one short transaction: the event ID goes into
/// processed_webhook_events first (ON CONFLICT DO NOTHING: a duplicate, even a concurrent one, finds it there and stops),
/// then the deposit is recorded and Sales confirms the reservation on the same transaction. Commit, then acknowledge.
/// A crash anywhere before the commit leaves nothing behind, so Stripe's retry processes the event once.
/// </summary>
// The gateway is resolved only when a refund is due: most events need no Stripe call, and a host without a Stripe key
// (CI, tests) still verifies and records webhooks.
internal sealed class StripeDepositWebhook(PaymentsDbContext db, IReservationDeposits reservations, IServiceProvider services, RequestTenantContext tenant, TimeProvider time)
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
        var (result, reservation) = await db.Database.CreateExecutionStrategy().ExecuteAsync(
            (stripeEvent, session, reservationId, tenantId),
            async (_, state, ct) => await ProcessAsync(state.stripeEvent, state.session, state.reservationId, state.tenantId, ct),
            verifySucceeded: null,
            cancellationToken);

        // C8 Lab 5: a refund is an external call, so it happens after the commit (Lab 4). Its intent is already stored;
        // if this call fails, Stripe redelivers the event, which lands here as a duplicate and finishes the refund.
        if (stripeEvent.Type == EventTypes.CheckoutSessionCompleted && result is WebhookResult.DepositRefunded or WebhookResult.Duplicate
            && await RefundPendingAsync(session.Id, cancellationToken))
        {
            result = WebhookResult.DepositRefunded;
        }

        return (result, reservation);
    }

    /// <summary>Refunds the session's deposit if its refund is recorded as intended but not done; true if it did.</summary>
    private async Task<bool> RefundPendingAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await db.Deposits.AsNoTracking().SingleOrDefaultAsync(d => d.SessionId == sessionId && d.RefundReason != null && d.RefundReference == null, cancellationToken) is not { } pending)
        {
            return false;
        }

        var refund = await services.GetRequiredService<IPaymentGateway>().RefundAsync(pending.ProviderReference, new Money(pending.Amount, pending.Currency), $"refund:{sessionId}", cancellationToken);
        await db.Deposits.Where(d => d.Id == pending.Id).ExecuteUpdateAsync(set => set
            .SetProperty(d => d.RefundReference, refund.Id)
            .SetProperty(d => d.RefundedAt, time.GetUtcNow()), cancellationToken);
        return true;
    }

    private async Task<(WebhookResult Result, ReservationId? Reservation)> ProcessAsync(Event stripeEvent, Session session, ReservationId reservationId, Guid tenantId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear(); // a retried attempt starts from nothing
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var first = await db.Database.ExecuteSqlAsync(
            $"INSERT INTO payments.processed_webhook_events (event_id, type, processed_at, tenant_id) VALUES ({stripeEvent.Id}, {stripeEvent.Type}, {time.GetUtcNow()}, {tenantId}) ON CONFLICT (event_id) DO NOTHING",
            cancellationToken);
        var repair = false;
        if (first == 0)
        {
            // Normally a duplicate. But a completed event whose deposit has gone (reconciliation's PaidNotRecorded) is
            // a repair: replaying the event from Stripe records the deposit again (C8 Lab 6).
            if (stripeEvent.Type != EventTypes.CheckoutSessionCompleted || await db.Deposits.AnyAsync(d => d.SessionId == session.Id, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (WebhookResult.Duplicate, reservationId);
            }

            repair = true;
        }

        var result = stripeEvent.Type == EventTypes.CheckoutSessionCompleted
            ? await CompletedAsync(session, reservationId, new DateTimeOffset(DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc)), transaction, cancellationToken)
            : await ExpiredAsync(session, reservationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (repair && result is WebhookResult.DepositAlreadyConfirmed or WebhookResult.DepositConfirmed ? WebhookResult.DepositRepaired : result, reservationId);
    }

    private async Task<WebhookResult> CompletedAsync(Session session, ReservationId reservationId, DateTimeOffset paidAt, IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        var paid = new DepositPaid(reservationId, StripeAmounts.FromMinorUnits(session.AmountTotal ?? 0, session.Currency), session.PaymentIntentId ?? session.Id, paidAt);
        var paidBefore = await db.Deposits.AnyAsync(d => d.ReservationId == reservationId && d.SessionId != session.Id && d.RefundReason == null, cancellationToken);
        var confirmation = await reservations.ConfirmPaidAsync(reservationId, paidAt, transaction.GetDbTransaction(), cancellationToken);

        // C8 Lab 5: decide in the transaction whether the money can be kept; the refund itself comes after the commit.
        var (result, refundReason) = confirmation.Outcome switch
        {
            DepositConfirmation.Confirmed => (WebhookResult.DepositConfirmed, null),
            DepositConfirmation.ReheldAndConfirmed => (WebhookResult.DepositReheldAndConfirmed, null),
            DepositConfirmation.AlreadyConfirmed when paidBefore => (WebhookResult.DepositRefunded, "The deposit for this reservation had already been paid, so this second payment"),
            DepositConfirmation.AlreadyConfirmed => (WebhookResult.DepositAlreadyConfirmed, null),
            _ => (WebhookResult.DepositRefunded, confirmation.Reason ?? "This payment could not be matched to a held reservation, so it"),
        };
        // C9 Lab 2: a kept deposit leaves the module as DepositPaid, through the outbox, in this transaction.
        if (result is WebhookResult.DepositConfirmed or WebhookResult.DepositReheldAndConfirmed)
        {
            var unit = Guid.TryParse(session.Metadata?.GetValueOrDefault(StripePaymentGateway.UnitMetadata), out var u) ? u : Guid.Empty;
            db.Set<OutboxMessage>().Add(OutboxMessage.For(IntegrationEvents.DepositPaid,
                new DepositPaidMessage(reservationId.Value, unit, tenant.TenantId!.Value, paid.Amount.Amount, paid.Amount.Currency, session.Id, paid.PaidAt),
                tenant.TenantId!.Value, time.GetUtcNow()));
        }

        db.Deposits.Add(new DepositRow
        {
            ReservationId = paid.ReservationId, SessionId = session.Id, ProviderReference = paid.ProviderReference,
            Amount = paid.Amount.Amount, Currency = paid.Amount.Currency, PaidAt = paid.PaidAt,
            RefundReason = refundReason is null ? null : refundReason.EndsWith('.') ? $"{refundReason} Your deposit has been refunded in full." : $"{refundReason} has been refunded in full.",
        });
        await db.SaveChangesAsync(cancellationToken);
        return result;
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
