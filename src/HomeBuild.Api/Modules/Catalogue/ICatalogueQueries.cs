using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue;

/// <summary>The only Catalogue type other modules may use.</summary>
public interface ICatalogueQueries
{
    Unit? FindUnit(UnitId unitId);

    /// <summary>The deposit strategy key ("fixed" or "percentage") of the unit's development.</summary>
    string DepositStrategyFor(DevelopmentId developmentId);
}
