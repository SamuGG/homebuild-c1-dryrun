using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;

namespace HomeBuild.Api.Tests;

/// <summary>Builders for the API's own aggregates (internal to the API, so they live with its tests).</summary>
internal static class CatalogueBuilders
{
    public static DevelopmentBuilder ADevelopment() => new();
}

internal sealed class DevelopmentBuilder
{
    private DevelopmentId id = DevelopmentId.New();
    private string name = "Riverside Test";
    private string town = "York";
    private string depositStrategy = "fixed";

    public DevelopmentBuilder WithId(DevelopmentId value)
    {
        id = value;
        return this;
    }

    public DevelopmentBuilder Named(string value)
    {
        name = value;
        return this;
    }

    public DevelopmentBuilder InTown(string value)
    {
        town = value;
        return this;
    }

    public DevelopmentBuilder WithDepositStrategy(string value)
    {
        depositStrategy = value;
        return this;
    }

    public Development Build() => new(id, name, town, depositStrategy);
}
