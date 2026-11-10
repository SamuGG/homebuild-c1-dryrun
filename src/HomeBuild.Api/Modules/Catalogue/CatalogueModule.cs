using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Catalogue.GetDevelopment;
using HomeBuild.Api.Modules.Catalogue.GetUnit;
using HomeBuild.Api.Modules.Catalogue.ListDevelopments;
using HomeBuild.Api.Modules.Catalogue.ListUnits;

namespace HomeBuild.Api.Modules.Catalogue;

public static class CatalogueModule
{
    public const string Name = "Catalogue";

    public static IServiceCollection AddCatalogueModule(this IServiceCollection services)
    {
        // Singleton: the in-memory store is the data; one instance for the app's life (thread-safe collections).
        services.AddSingleton<InMemoryCatalogueStore>();
        // Singleton: forwards to the same store instance, so slices and Sales see the same data.
        services.AddSingleton<ICatalogueStore>(sp => sp.GetRequiredService<InMemoryCatalogueStore>());
        // Singleton: the public read surface for other modules; same instance, no per-request state.
        services.AddSingleton<ICatalogueQueries>(sp => sp.GetRequiredService<InMemoryCatalogueStore>());
        return services;
    }

    public static IEndpointRouteBuilder MapCatalogueModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("").WithTags(Name).AddEndpointFilter<RejectDefaultUnitIdFilter>();
        group.MapListDevelopments();
        group.MapGetDevelopment();
        group.MapListUnits();
        group.MapGetUnit();
        return endpoints;
    }
}
