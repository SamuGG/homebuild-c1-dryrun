using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Catalogue;

internal interface ICatalogueStore
{
    Task<IReadOnlyList<Development>> DevelopmentsAsync(CancellationToken cancellationToken);

    Task<Development?> FindDevelopmentAsync(DevelopmentId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Unit>> UnitsInAsync(DevelopmentId id, CancellationToken cancellationToken);

    Task<Unit?> FindUnitAsync(UnitId id, CancellationToken cancellationToken);
}

/// <summary>Read-only access to the catalogue. Scoped, like the DbContext it wraps; every read is untracked.</summary>
internal sealed class EfCatalogueStore(CatalogueDbContext db) : ICatalogueStore, ICatalogueQueries
{
    public async Task<IReadOnlyList<Development>> DevelopmentsAsync(CancellationToken cancellationToken) =>
        await db.Developments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(cancellationToken);

    public Task<Development?> FindDevelopmentAsync(DevelopmentId id, CancellationToken cancellationToken) =>
        db.Developments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Unit>> UnitsInAsync(DevelopmentId id, CancellationToken cancellationToken) =>
        await db.Units.AsNoTracking().Where(u => u.DevelopmentId == id).OrderBy(u => u.PlotNumber).ToListAsync(cancellationToken);

    public Task<Unit?> FindUnitAsync(UnitId id, CancellationToken cancellationToken) =>
        db.Units.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<string> DepositStrategyForAsync(DevelopmentId developmentId, CancellationToken cancellationToken = default) =>
        await db.Developments.Where(d => d.Id == developmentId).Select(d => d.DepositStrategy).SingleOrDefaultAsync(cancellationToken)
        ?? "fixed";
}
