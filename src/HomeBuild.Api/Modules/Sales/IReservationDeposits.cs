using System.Data.Common;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Sales;

public enum DepositConfirmation
{
    /// <summary>The reservation was held and is now confirmed.</summary>
    Confirmed,

    /// <summary>Already confirmed (a duplicate, or another payment got there first): nothing changed.</summary>
    AlreadyConfirmed,

    /// <summary>Expired or cancelled: the money arrived but the reservation cannot take it (late payments, Lab 5).</summary>
    NotConfirmable,

    NotFound,
}

public sealed record DepositConfirmationResult(DepositConfirmation Outcome, string State);

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
internal sealed class EfReservationDeposits(SalesDbContext db) : IReservationDeposits
{
    public async Task<DepositConfirmationResult> ConfirmPaidAsync(ReservationId id, DateTimeOffset paidAt, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        db.Database.SetDbConnection(transaction.Connection);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        // Read the current state, never assume it: events arrive late, twice and out of order.
        if (await db.Reservations.SingleOrDefaultAsync(r => r.Id == id, cancellationToken) is not { } reservation)
        {
            return new(DepositConfirmation.NotFound, "");
        }

        if (reservation.State is Confirmed)
        {
            return new(DepositConfirmation.AlreadyConfirmed, nameof(Confirmed));
        }

        if (reservation.Confirm(paidAt).Value is not Transitioned)
        {
            return new(DepositConfirmation.NotConfirmable, reservation.State.GetType().Name);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new(DepositConfirmation.Confirmed, nameof(Confirmed));
    }
}
