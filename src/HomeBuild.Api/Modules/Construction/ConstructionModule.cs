using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Construction.GetBoard;
using HomeBuild.Api.Modules.Construction.GetBuyerProgress;

namespace HomeBuild.Api.Modules.Construction;

/// <summary>Build progress (C5: a seeded read model). Uses Catalogue and Sales only through their published queries.</summary>
public static class ConstructionModule
{
    public const string Name = "Construction";

    public static IServiceCollection AddConstructionModule(this IServiceCollection services)
    {
        // Singleton: the seeded read model is immutable.
        services.AddSingleton<MilestoneReadModel>();
        return services;
    }

    public static IEndpointRouteBuilder MapConstructionModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapVersionedGroup().WithTags(Name);
        group.MapGetBoard();
        group.MapGetBuyerProgress();
        return endpoints;
    }
}
