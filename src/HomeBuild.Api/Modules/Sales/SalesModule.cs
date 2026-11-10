using HomeBuild.Api.Modules.Sales.CancelReservation;
using HomeBuild.Api.Modules.Sales.GetReservation;
using HomeBuild.Api.Modules.Sales.ReserveUnit;

namespace HomeBuild.Api.Modules.Sales;

public static class SalesModule
{
    public const string Name = "Sales";

    public static IServiceCollection AddSalesModule(this IServiceCollection services)
    {
        services.AddReservationPolicy();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IReservationStore, InMemoryReservationStore>();
        return services;
    }

    public static IEndpointRouteBuilder MapSalesModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("").WithTags(Name);
        group.MapReserveUnit();
        group.MapGetReservation();
        group.MapCancelReservation();
        return endpoints;
    }
}
