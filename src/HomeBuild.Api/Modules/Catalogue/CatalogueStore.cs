using System.Collections.Concurrent;
using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue;

internal interface ICatalogueStore
{
    IReadOnlyList<Development> Developments();

    Development? FindDevelopment(DevelopmentId id);

    IReadOnlyList<Unit> UnitsIn(DevelopmentId id);

    Unit? FindUnit(UnitId id);
}

/// <summary>In-memory catalogue seeded with fixed IDs so external tests can address them.</summary>
internal sealed class InMemoryCatalogueStore : ICatalogueStore, ICatalogueQueries
{
    public static readonly DevelopmentId Riverside = new(Guid.Parse("0198a000-0000-7000-8000-000000000001"));
    public static readonly DevelopmentId Hillcrest = new(Guid.Parse("0198a000-0000-7000-8000-000000000002"));

    private readonly ConcurrentDictionary<DevelopmentId, Development> _developments = new();
    private readonly ConcurrentDictionary<UnitId, Unit> _units = new();

    public InMemoryCatalogueStore()
    {
        _developments[Riverside] = new Development(Riverside, "Riverside", "York");
        _developments[Hillcrest] = new Development(Hillcrest, "Hillcrest", "Leeds");
        for (var plot = 1; plot <= 3; plot++)
        {
            AddUnit(Riverside, plot, 250_000m + (plot * 10_000m));
            AddUnit(Hillcrest, plot, 320_000m + (plot * 10_000m));
        }
    }

    public static UnitId UnitIdFor(DevelopmentId development, int plot) =>
        new(Guid.Parse($"0198a000-0000-7000-8{(development == Riverside ? 1 : 2):000}-{plot:000000000000}"));

    public IReadOnlyList<Development> Developments() => [.. _developments.Values.OrderBy(d => d.Name, StringComparer.Ordinal)];

    public Development? FindDevelopment(DevelopmentId id) => _developments.GetValueOrDefault(id);

    public IReadOnlyList<Unit> UnitsIn(DevelopmentId id) =>
        [.. _units.Values.Where(u => u.DevelopmentId == id).OrderBy(u => u.PlotNumber, StringComparer.Ordinal)];

    public Unit? FindUnit(UnitId id) => _units.GetValueOrDefault(id);

    private void AddUnit(DevelopmentId development, int plot, decimal price)
    {
        var id = UnitIdFor(development, plot);
        _units[id] = new Unit(id, development, $"P{plot}", new Money(price, "GBP"));
    }
}
