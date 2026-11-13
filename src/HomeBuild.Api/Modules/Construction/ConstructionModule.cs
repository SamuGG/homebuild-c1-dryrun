using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Construction.GetBoard;
using HomeBuild.Api.Modules.Construction.GetBuyerProgress;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Construction;

/// <summary>Build progress (C7: the BuildProgress aggregate). Uses Catalogue and Sales only through their published queries.</summary>
public static class ConstructionModule
{
    public const string Name = "Construction";

    public static IServiceCollection AddConstructionModule(this IServiceCollection services)
    {
        // Scoped (AddDbContext's default): a DbContext is not thread-safe and tracks entities per request.
        services.AddDbContext<ConstructionDbContext>((sp, options) => Configure(options, HomeBuildDatabase.ConnectionString(sp))
            .AddInterceptors(new TenantCommandInterceptor(sp.GetRequiredService<ITenantContext>())));
        // Scoped: wraps the scoped DbContext.
        services.AddScoped<BuildProgressReader>();
        return services;
    }

    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseHomeBuildDatabase(connectionString, ConstructionDbContext.Schema)
            .UseSeeding((db, _) => ConstructionDbContext.SeedAsync(db, CancellationToken.None).GetAwaiter().GetResult())
            .UseAsyncSeeding((db, _, ct) => ConstructionDbContext.SeedAsync(db, ct));

    /// <summary>A context outside a request, for tools and tests; <paramref name="tenantId"/> null means no tenant.</summary>
    internal static ConstructionDbContext CreateDbContext(string connectionString, Guid? tenantId = null)
    {
        var tenant = new FixedTenantContext(tenantId);
        return new((DbContextOptions<ConstructionDbContext>)Configure(new DbContextOptionsBuilder<ConstructionDbContext>(), connectionString)
            .AddInterceptors(new TenantCommandInterceptor(tenant)).Options, tenant);
    }

    public static IEndpointRouteBuilder MapConstructionModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapVersionedGroup().WithTags(Name);
        group.MapGetBoard();
        group.MapGetBuyerProgress();
        return endpoints;
    }
}
