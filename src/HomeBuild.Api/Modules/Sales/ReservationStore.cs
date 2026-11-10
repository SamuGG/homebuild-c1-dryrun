using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Sales;

internal interface IReservationStore
{
    Task<Unit?> FindUnitAsync(UnitId id, CancellationToken cancellationToken);

    Task<Reservation?> FindAsync(ReservationId id, CancellationToken cancellationToken);

    void Add(Reservation reservation);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Scoped over <see cref="SalesDbContext"/>: the unit and its reservation are saved in one transaction.</summary>
internal sealed class EfReservationStore(SalesDbContext db) : IReservationStore, IUnitAvailability
{
    public Task<Unit?> FindUnitAsync(UnitId id, CancellationToken cancellationToken) =>
        db.Units.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<Reservation?> FindAsync(ReservationId id, CancellationToken cancellationToken) =>
        db.Reservations.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(Reservation reservation) => db.Reservations.Add(reservation);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<UnitId, UnitStatus>> StatusOfAsync(
        IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken = default) =>
        await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Status, cancellationToken);
}
