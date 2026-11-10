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
        // Singleton: the in-memory store is the data (thread-safe collections); C3 makes it scoped with EF Core.
        services.AddSingleton<IReservationStore, InMemoryReservationStore>();
        // Keyed singletons: stateless strategies, one per key, picked per development.
        services.AddKeyedSingleton<IDepositStrategy, FixedDepositStrategy>(DepositStrategyKeys.Fixed);
        services.AddKeyedSingleton<IDepositStrategy, PercentageDepositStrategy>(DepositStrategyKeys.Percentage);
        // Singleton: holds only the two singleton strategies.
        services.AddSingleton<DepositStrategySelector>();
        return services;
    }

    public static IEndpointRouteBuilder MapSalesModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("").WithTags(Name).AddEndpointFilter<RejectDefaultUnitIdFilter>();
        group.MapReserveUnit();
        group.MapGetReservation();
        group.MapCancelReservation();
        return endpoints;
    }
}
