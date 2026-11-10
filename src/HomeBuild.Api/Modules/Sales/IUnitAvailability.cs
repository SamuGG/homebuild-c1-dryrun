using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales;

/// <summary>Sales' published read surface: other modules ask it whether units are available.</summary>
public interface IUnitAvailability
{
    Task<IReadOnlyDictionary<UnitId, UnitStatus>> StatusOfAsync(IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken = default);
}
