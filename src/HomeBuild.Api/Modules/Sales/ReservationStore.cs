using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HomeBuild.Api.Modules.Sales;

internal interface IReservationStore
{
    Task<Unit?> FindUnitAsync(UnitId id, CancellationToken cancellationToken);

    Task<Reservation?> FindAsync(ReservationId id, CancellationToken cancellationToken);

    Task<Reservation?> FindReadOnlyAsync(ReservationId id, CancellationToken cancellationToken);

    void Add(Reservation reservation);

    /// <summary>
    /// Saves the reserved unit and its new reservation in one transaction. A stale unit version (another buyer won)
    /// or the one-active-reservation index both come back as <see cref="UnitUnavailable"/>.
    /// </summary>
    Task<ReserveResult> ReserveAndSaveAsync(
        Unit unit, BuyerId buyer, Money deposit, ReservationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Scoped over <see cref="SalesDbContext"/>: the unit and its reservation are saved in one transaction.</summary>
internal sealed class EfReservationStore(SalesDbContext db) : IReservationStore, IUnitAvailability
{
    public Task<Unit?> FindUnitAsync(UnitId id, CancellationToken cancellationToken) =>
        db.Units.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<Reservation?> FindAsync(ReservationId id, CancellationToken cancellationToken) =>
        db.Reservations.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<Reservation?> FindReadOnlyAsync(ReservationId id, CancellationToken cancellationToken) =>
        db.Reservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(Reservation reservation) => db.Reservations.Add(reservation);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public async Task<ReserveResult> ReserveAndSaveAsync(
        Unit unit, BuyerId buyer, Money deposit, ReservationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var result = unit.Reserve(buyer, deposit, policy, now);
        if (result.Value is not Reserved reserved)
        {
            return result;
        }

        db.Reservations.Add(reserved.Reservation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return reserved;
        }
        catch (DbUpdateConcurrencyException)
        {
            return new UnitUnavailable(unit.Id, UnitStatus.Reserved);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: SalesDbContext.ActiveReservationIndex,
        })
        {
            return new UnitUnavailable(unit.Id, UnitStatus.Reserved);
        }
    }

    public async Task<IReadOnlyDictionary<UnitId, UnitStatus>> StatusOfAsync(
        IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken = default) =>
        await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Status, cancellationToken);

    public Task<LatestReservation?> LatestReservationAsync(UnitId unitId, CancellationToken cancellationToken = default) =>
        db.Reservations.AsNoTracking()
            .Where(r => r.UnitId == unitId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new LatestReservation(r.UnitId, r.Id, EF.Property<string>(r, "Status"), r.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<UnitId, LatestReservation>> LatestReservationsAsync(
        IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken = default)
    {
        var latest = await db.Reservations.AsNoTracking()
            .Where(r => unitIds.Contains(r.UnitId))
            .GroupBy(r => r.UnitId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt)
                .Select(r => new LatestReservation(r.UnitId, r.Id, EF.Property<string>(r, "Status"), r.CreatedAt))
                .First())
            .ToListAsync(cancellationToken);
        return latest.ToDictionary(l => l.UnitId);
    }
}
