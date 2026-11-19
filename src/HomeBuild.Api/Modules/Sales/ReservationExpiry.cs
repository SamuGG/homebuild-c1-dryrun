using System.Globalization;
using HomeBuild.Api.Data;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using HomeBuild.Messaging;

namespace HomeBuild.Api.Modules.Sales;

/// <summary>What a ReservationExpiryDue message found (C9 Lab 6).</summary>
internal enum ExpiryResult
{
    Expired,

    /// <summary>Paid, cancelled or already expired: the schedule fired after the world moved on; nothing to do.</summary>
    NotHeld,

    /// <summary>Still held, and the hold has not ended yet (the message came early).</summary>
    NotYetDue,

    NotFound,
}

/// <summary>
/// Expires held reservations past their hold period without loading them, and (C9 Lab 2) records a ReservationExpired
/// for each in the outbox, in the same transaction. C9 Lab 6: <see cref="ExpireDueAsync"/> expires one reservation when
/// its schedule fires; <see cref="ExpireStaleAsync"/> stays as the hourly catch-all. Both change only a reservation that
/// is still held and past its end, so a late, repeated or stray message does nothing.
/// </summary>
internal sealed class ReservationExpiry(SalesDbContext db)
{
    /// <returns>The number of reservations expired.</returns>
    public Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken) => ExpireAsync(null, now, cancellationToken);

    public async Task<ExpiryResult> ExpireDueAsync(ReservationId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await ExpireAsync(id.Value, now, cancellationToken) == 1)
        {
            return ExpiryResult.Expired;
        }

        db.ChangeTracker.Clear();
        return await db.Reservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken) switch
        {
            null => ExpiryResult.NotFound,
            { State: Held held } when held.ExpiresAt > now => ExpiryResult.NotYetDue,
            _ => ExpiryResult.NotHeld,
        };
    }

    private Task<int> ExpireAsync(Guid? only, DateTimeOffset now, CancellationToken cancellationToken) =>
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
                    WHERE status = 'held' AND expires_at <= @now AND (@only::uuid IS NULL OR id = @only::uuid)
                    RETURNING id, unit_id, buyer_id, tenant_id
                    """, new Npgsql.NpgsqlParameter("state", state), new Npgsql.NpgsqlParameter("now", now),
                    new Npgsql.NpgsqlParameter("only", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)only ?? DBNull.Value })
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
