using System.Globalization;
using HomeBuild.Api.Data;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using HomeBuild.Messaging;

namespace HomeBuild.Api.Modules.Sales;

/// <summary>
/// Expires held reservations past their hold period without loading them, and (C9 Lab 2) records a ReservationExpired
/// for each in the outbox, in the same transaction. Lab 6 adds per-reservation schedules; this sweep stays as the catch-all.
/// </summary>
internal sealed class ReservationExpiry(SalesDbContext db)
{
    /// <returns>The number of reservations expired.</returns>
    public Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // One UPDATE for every stale reservation, returning which ones it expired (the state column is how
            // ReservationStateConverter stores Expired: "Expired|<at>"). Sent through EF (SqlQueryRaw, not composed,
            // so it runs as written), so EF's interceptors and logging see it like any other command.
            var state = "Expired|" + now.ToString("O", CultureInfo.InvariantCulture);
            var expired = (await db.Database.SqlQueryRaw<ExpiredRow>($"""
                    UPDATE {SalesDbContext.Schema}.reservations SET state = @state
                    WHERE status = 'held' AND expires_at <= @now
                    RETURNING id, unit_id, buyer_id, tenant_id
                    """, new Npgsql.NpgsqlParameter("state", state), new Npgsql.NpgsqlParameter("now", now))
                .ToListAsync(ct))
                .Select(r => new ReservationExpiredMessage(r.Id, r.UnitId, r.BuyerId, r.TenantId, now)).ToList();

            db.Set<OutboxMessage>().AddRange(expired.Select(e => OutboxMessage.For(IntegrationEvents.ReservationExpired, e, e.TenantId, now)));
            await db.SaveChangesAsync(ct);

            // Release the units those reservations held (a second statement: units live in another table).
            await db.Units
                .Where(u => u.Status == UnitStatus.Reserved
                            && !db.Reservations.Any(r => r.UnitId == u.Id && (EF.Property<string>(r, "Status") == "held" || EF.Property<string>(r, "Status") == "confirmed")))
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.Status, UnitStatus.Available), ct);
            await transaction.CommitAsync(ct);
            return expired.Count;
        }, cancellationToken);

    private sealed record ExpiredRow(Guid Id, Guid UnitId, Guid BuyerId, Guid TenantId);
}
