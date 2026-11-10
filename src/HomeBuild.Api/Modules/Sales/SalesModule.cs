using HomeBuild.Api.Data;
using Microsoft.EntityFrameworkCore;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales.CancelReservation;
using HomeBuild.Api.Modules.Sales.GetReservation;
using HomeBuild.Api.Modules.Sales.ReserveUnit;

namespace HomeBuild.Api.Modules.Sales;

public static class SalesModule
{
    public const string Name = "Sales";

    public static IServiceCollection AddSalesModule(this IServiceCollection services)
    {
        // Options, validator and ReservationPolicy: singletons, see AddReservationPolicy.
        services.AddReservationPolicy();
        // Singleton: the system clock is stateless and thread-safe.
        services.AddSingleton(TimeProvider.System);
        // Scoped (AddDbContext's default): a DbContext is not thread-safe and tracks entities per request.
        services.AddDbContext<SalesDbContext>((sp, options) => Configure(options, HomeBuildDatabase.ConnectionString(sp)));
        // Scoped: wraps the scoped DbContext.
        services.AddScoped<EfReservationStore>();
        services.AddScoped<IReservationStore>(sp => sp.GetRequiredService<EfReservationStore>());
        // Scoped: Sales' public availability reads, for Catalogue.
        services.AddScoped<IUnitAvailability>(sp => sp.GetRequiredService<EfReservationStore>());
        // Scoped: runs one bulk update over the scoped context.
        services.AddScoped<ReservationExpiry>();
        // Keyed singletons: stateless strategies, one per key, picked per development.
        services.AddKeyedSingleton<IDepositStrategy, FixedDepositStrategy>(DepositStrategyKeys.Fixed);
        services.AddKeyedSingleton<IDepositStrategy, PercentageDepositStrategy>(DepositStrategyKeys.Percentage);
        // Singleton: holds only the two singleton strategies.
        services.AddSingleton<DepositStrategySelector>();
        return services;
    }

    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseHomeBuildDatabase(connectionString, SalesDbContext.Schema)
            .UseSeeding((db, _) => SalesDbContext.SeedAsync(db, CancellationToken.None).GetAwaiter().GetResult())
            .UseAsyncSeeding((db, _, ct) => SalesDbContext.SeedAsync(db, ct));

    internal static SalesDbContext CreateDbContext(string connectionString) =>
        new((DbContextOptions<SalesDbContext>)Configure(new DbContextOptionsBuilder<SalesDbContext>(), connectionString).Options);

    public static IEndpointRouteBuilder MapSalesModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapVersionedGroup().WithTags(Name).AddEndpointFilter<RejectDefaultUnitIdFilter>();
        group.MapReserveUnit();
        group.MapGetReservation();
        group.MapCancelReservation();
        return endpoints;
    }
}
