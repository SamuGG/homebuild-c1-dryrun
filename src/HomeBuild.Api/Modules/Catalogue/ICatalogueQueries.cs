using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue;

/// <summary>A unit as other modules see it: its plot and development, nothing else.</summary>
public sealed record PlotSummary(UnitId UnitId, string PlotNumber, DevelopmentId DevelopmentId, string DevelopmentName);

/// <summary>The only Catalogue type other modules may use.</summary>
public interface ICatalogueQueries
{
    /// <summary>The deposit strategy key ("fixed" or "percentage") of a development.</summary>
    Task<string> DepositStrategyForAsync(DevelopmentId developmentId, CancellationToken cancellationToken = default);

    /// <summary>A development's plots in plot order, or null if the development is not in the caller's catalogue.</summary>
    Task<IReadOnlyList<PlotSummary>?> PlotsInAsync(DevelopmentId developmentId, CancellationToken cancellationToken = default);

    /// <summary>The plots for these units (units not in the caller's catalogue are missing).</summary>
    Task<IReadOnlyDictionary<UnitId, PlotSummary>> PlotsAsync(IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken = default);
}
