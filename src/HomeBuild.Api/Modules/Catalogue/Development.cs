using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue;

internal sealed record Development(DevelopmentId Id, string Name, string Town, string DepositStrategy)
{
    // Navigations for Catalogue's read queries (Lab 4). Units are the domain type; the domain itself has no navigations.
    public List<Unit> Units { get; init; } = [];

    public List<UnitPrice> PriceHistory { get; init; } = [];
}

/// <summary>One price a unit had from a point in time. Catalogue-owned history, newest last.</summary>
internal sealed record UnitPrice(Guid Id, UnitId UnitId, DevelopmentId DevelopmentId, decimal Amount, string Currency, DateTimeOffset EffectiveFrom);
