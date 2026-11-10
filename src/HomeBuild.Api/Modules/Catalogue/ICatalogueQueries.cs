using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue;

/// <summary>The only Catalogue type other modules may use.</summary>
public interface ICatalogueQueries
{
    /// <summary>The deposit strategy key ("fixed" or "percentage") of a development.</summary>
    Task<string> DepositStrategyForAsync(DevelopmentId developmentId, CancellationToken cancellationToken = default);
}
