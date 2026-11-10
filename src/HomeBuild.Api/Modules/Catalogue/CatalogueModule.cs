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
        services.AddSingleton<InMemoryCatalogueStore>();
        services.AddSingleton<ICatalogueStore>(sp => sp.GetRequiredService<InMemoryCatalogueStore>());
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
