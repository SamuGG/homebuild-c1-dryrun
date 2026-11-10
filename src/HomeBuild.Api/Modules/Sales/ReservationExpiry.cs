using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Sales;

/// <summary>Expires held reservations past their hold period without loading them. C10 runs it on a schedule.</summary>
internal sealed class ReservationExpiry(SalesDbContext db)
{
    /// <returns>The number of reservations expired.</returns>
    public async Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // One UPDATE for every stale reservation. ExecuteUpdate skips the change tracker and the unit's row version,
        // which is safe here because it only moves held reservations out of the active set.
        var expired = await db.Reservations
            .Where(r => EF.Property<string>(r, "Status") == "held" && r.ExpiresAt <= now)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.State, new Expired(now)), cancellationToken);

        // Release the units those reservations held (a second statement: units live in another table).
        await db.Units
            .Where(u => u.Status == UnitStatus.Reserved
                        && !db.Reservations.Any(r => r.UnitId == u.Id && (EF.Property<string>(r, "Status") == "held" || EF.Property<string>(r, "Status") == "confirmed")))
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.Status, UnitStatus.Available), cancellationToken);
        return expired;
    }
}
