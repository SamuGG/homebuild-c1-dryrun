using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue;

/// <summary>A unit as other modules see it: its plot and development, nothing else.</summary>
/// <summary>A unit's list price and development, as other modules see it (C7 Lab 5: Payments' stage schedules).</summary>
public sealed record UnitListPrice(UnitId UnitId, DevelopmentId DevelopmentId, Money Price);

public sealed record PlotSummary(UnitId UnitId, string PlotNumber, DevelopmentId DevelopmentId, string DevelopmentName);

/// <summary>The only Catalogue type other modules may use.</summary>
public interface ICatalogueQueries
{
    /// <summary>The deposit strategy key ("fixed" or "percentage") of a development.</summary>
    /// <summary>A unit's list price and development, or null if the unit is not in the caller's catalogue.</summary>
    Task<UnitListPrice?> PriceOfAsync(UnitId unitId, CancellationToken cancellationToken = default);

    Task<string> DepositStrategyForAsync(DevelopmentId developmentId, CancellationToken cancellationToken = default);

    /// <summary>A development's plots in plot order, or null if the development is not in the caller's catalogue.</summary>
    Task<IReadOnlyList<PlotSummary>?> PlotsInAsync(DevelopmentId developmentId, CancellationToken cancellationToken = default);

    /// <summary>The plots for these units (units not in the caller's catalogue are missing).</summary>
    Task<IReadOnlyDictionary<UnitId, PlotSummary>> PlotsAsync(IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken = default);
}
