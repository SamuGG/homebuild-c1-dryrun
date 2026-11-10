using HomeBuild.Api.Data;
using Microsoft.EntityFrameworkCore;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Catalogue.GetDevelopment;
using HomeBuild.Api.Modules.Catalogue.GetDevelopmentPage;
using HomeBuild.Api.Modules.Catalogue.GetUnit;
using HomeBuild.Api.Modules.Catalogue.ListDevelopments;
using HomeBuild.Api.Modules.Catalogue.ListUnits;
using HomeBuild.Api.Modules.Catalogue.SearchUnits;

namespace HomeBuild.Api.Modules.Catalogue;

public static class CatalogueModule
{
    public const string Name = "Catalogue";

    public static IServiceCollection AddCatalogueModule(this IServiceCollection services)
    {
        // Scoped (AddDbContext's default): a DbContext is not thread-safe and tracks entities per request.
        services.AddDbContext<CatalogueDbContext>((sp, options) => Configure(options, HomeBuildDatabase.ConnectionString(sp)));
        // Scoped: wraps the scoped DbContext.
        services.AddScoped<EfCatalogueStore>();
        services.AddScoped<ICatalogueStore>(sp => sp.GetRequiredService<EfCatalogueStore>());
        // Scoped: the public read surface for other modules, over the same context.
        services.AddScoped<ICatalogueQueries>(sp => sp.GetRequiredService<EfCatalogueStore>());
        // Singleton: the cursor protector is stateless; Data Protection keeps its own key ring.
        services.AddDataProtection();
        services.AddSingleton<UnitsCursor>();
        return services;
    }

    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseHomeBuildDatabase(connectionString, CatalogueDbContext.Schema)
            .UseSeeding((db, _) => CatalogueDbContext.SeedAsync(db, CancellationToken.None).GetAwaiter().GetResult())
            .UseAsyncSeeding((db, _, ct) => CatalogueDbContext.SeedAsync(db, ct));

    internal static CatalogueDbContext CreateDbContext(string connectionString) =>
        new((DbContextOptions<CatalogueDbContext>)Configure(new DbContextOptionsBuilder<CatalogueDbContext>(), connectionString).Options);

    public static IEndpointRouteBuilder MapCatalogueModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapVersionedGroup().WithTags(Name).AddEndpointFilter<RejectDefaultUnitIdFilter>();
        group.MapListDevelopments();
        group.MapGetDevelopment();
        group.MapGetDevelopmentPage();
        group.MapListUnits();
        group.MapSearchUnits();
        group.MapGetUnit();
        return endpoints;
    }
}
