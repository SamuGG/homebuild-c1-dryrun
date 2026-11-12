using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Catalogue;

internal interface ICatalogueStore
{
    Task<IReadOnlyList<Development>> DevelopmentsAsync(CancellationToken cancellationToken);

    Task<Development?> FindDevelopmentAsync(DevelopmentId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Unit>> UnitsInAsync(DevelopmentId id, CancellationToken cancellationToken);

    Task<Unit?> FindUnitAsync(UnitId id, CancellationToken cancellationToken);

    /// <summary>The unit's marketing description (a shadow property), or "" if there is none.</summary>
    Task<string> DescriptionOfAsync(UnitId id, CancellationToken cancellationToken);
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

    public async Task<string> DescriptionOfAsync(UnitId id, CancellationToken cancellationToken) =>
        await db.Units.Where(u => u.Id == id).Select(u => EF.Property<string>(u, CatalogueDbContext.UnitDescription)).SingleOrDefaultAsync(cancellationToken) ?? "";

    public async Task<IReadOnlyList<PlotSummary>?> PlotsInAsync(DevelopmentId developmentId, CancellationToken cancellationToken = default)
    {
        if (await db.Developments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == developmentId, cancellationToken) is not { } development)
        {
            return null;
        }

        var units = await db.Units.AsNoTracking().Where(u => u.DevelopmentId == developmentId).OrderBy(u => u.PlotNumber).ToListAsync(cancellationToken);
        return [.. units.Select(u => new PlotSummary(u.Id, u.PlotNumber, development.Id, development.Name))];
    }

    public async Task<IReadOnlyDictionary<UnitId, PlotSummary>> PlotsAsync(IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken = default) =>
        await (from u in db.Units.AsNoTracking()
               join d in db.Developments.AsNoTracking() on u.DevelopmentId equals d.Id
               where unitIds.Contains(u.Id)
               select new PlotSummary(u.Id, u.PlotNumber, d.Id, d.Name))
            .ToDictionaryAsync(p => p.UnitId, cancellationToken);

    public async Task<string> DepositStrategyForAsync(DevelopmentId developmentId, CancellationToken cancellationToken = default) =>
        await db.Developments.Where(d => d.Id == developmentId).Select(d => d.DepositStrategy).SingleOrDefaultAsync(cancellationToken)
        ?? "fixed";
}
