using System.Data.Common;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Sales;

public enum DepositConfirmation
{
    /// <summary>The reservation was held and is now confirmed.</summary>
    Confirmed,

    /// <summary>C8 Lab 5: the hold had ended, but the unit was still free: held again and confirmed.</summary>
    ReheldAndConfirmed,

    /// <summary>Already confirmed (a duplicate, or another payment got there first): nothing changed.</summary>
    AlreadyConfirmed,

    /// <summary>Cancelled, or expired with the unit taken: the money arrived but the reservation cannot take it.</summary>
    NotConfirmable,

    NotFound,
}

/// <param name="Reason">Why a payment cannot be kept (NotConfirmable, NotFound), in words the buyer reads.</param>
public sealed record DepositConfirmationResult(DepositConfirmation Outcome, string State, string? Reason = null);

/// <summary>
/// Sales' published write for Payments (C8 Lab 2): confirm a reservation whose deposit Stripe says is paid, inside the
/// caller's database transaction, so the webhook's record of the event and the confirmation commit or roll back
/// together. Both modules' tables are in one database; C9's outbox replaces this shared transaction.
/// </summary>
public interface IReservationDeposits
{
    Task<DepositConfirmationResult> ConfirmPaidAsync(ReservationId id, DateTimeOffset paidAt, DbTransaction transaction, CancellationToken cancellationToken = default);
}

/// <summary>Over the request's <see cref="SalesDbContext"/>, enlisted in the caller's connection and transaction.</summary>
internal sealed class EfReservationDeposits(SalesDbContext db, ReservationPolicy policy, TimeProvider time) : IReservationDeposits
{
    public async Task<DepositConfirmationResult> ConfirmPaidAsync(ReservationId id, DateTimeOffset paidAt, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        // Joins the caller's connection and transaction. Under the execution strategy the caller may run this again on
        // a retry (a new transaction, same connection), so nothing from a failed attempt is kept.
        db.ChangeTracker.Clear();
        if (!ReferenceEquals(db.Database.GetDbConnection(), transaction.Connection))
        {
            db.Database.SetDbConnection(transaction.Connection);
        }

        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        // Read the current state, never assume it: events arrive late, twice and out of order.
        if (await db.Reservations.SingleOrDefaultAsync(r => r.Id == id, cancellationToken) is not { } reservation)
        {
            return new(DepositConfirmation.NotFound, "", "No matching reservation was found.");
        }

        if (reservation.State is Confirmed)
        {
            return new(DepositConfirmation.AlreadyConfirmed, nameof(Confirmed));
        }

        if (reservation.Confirm(paidAt).Value is Transitioned)
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(DepositConfirmation.Confirmed, nameof(Confirmed));
        }

        // C8 Lab 5, late payments: the hold ended before the payment.
        var now = time.GetUtcNow();
        var unit = await db.Units.SingleAsync(u => u.Id == reservation.UnitId, cancellationToken);
        if (reservation.State is Held)
        {
            // Past its end but not yet expired by the job: the unit is still this reservation's.
            reservation.Expire(now);
        }
        else if (reservation.State is Expired && unit.Status == UnitStatus.Available)
        {
            unit.Hold(); // the unit's row version and the one-active-reservation index decide a race with a new buyer
        }
        else
        {
            return new(DepositConfirmation.NotConfirmable, reservation.State.GetType().Name, reservation.State is Cancelled
                ? "The reservation was cancelled before your payment arrived."
                : "The hold ended before your payment arrived, and another buyer has since reserved this home.");
        }

        if (reservation.Reinstate(now + policy.HoldPeriod).Value is not Transitioned || reservation.Confirm(paidAt).Value is not Transitioned)
        {
            throw new InvalidOperationException($"Reservation {id} could not be held again and confirmed.");
        }

        await db.SaveChangesAsync(cancellationToken);
        return new(DepositConfirmation.ReheldAndConfirmed, nameof(Confirmed));
    }
}
