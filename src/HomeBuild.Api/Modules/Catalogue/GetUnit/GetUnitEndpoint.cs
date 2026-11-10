using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Catalogue.GetUnit;

/// <summary>v1 shape: price as a number plus a separate currency. Deprecated.</summary>
internal sealed record UnitResponse(Guid Id, Guid DevelopmentId, string PlotNumber, decimal Price, string Currency, string Status);

/// <summary>v2 shape: price as an object.</summary>
internal sealed record UnitResponseV2(Guid Id, Guid DevelopmentId, string PlotNumber, PriceResponse Price, string Status);

internal sealed record PriceResponse(decimal Amount, string Currency);

internal static class GetUnitEndpoint
{
    public static void MapGetUnit(this RouteGroupBuilder group)
    {
        group.MapGet("/units/{unitId}", HandleV1)
            .MapToApiVersion(ApiVersions.V1)
            .WithName("GetUnit")
            .WithSummary("Get a unit (v1: price and currency as separate fields)")
            .ProducesProblem(StatusCodes.Status400BadRequest); // the default-ID filter answers before the handler
        group.MapGet("/units/{unitId}", HandleV2)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("GetUnitV2")
            .WithSummary("Get a unit (v2: price as { amount, currency })")
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    // Both versions share the lookup and differ only in mapping.
    private static async Task<Results<Ok<UnitResponse>, NotFound>> HandleV1(
        UnitId unitId, ICatalogueStore store, IUnitAvailability availability, CancellationToken cancellationToken) =>
        await FindAsync(unitId, store, availability, cancellationToken) is var (u, status) && u is not null
            ? TypedResults.Ok(new UnitResponse(u.Id.Value, u.DevelopmentId.Value, u.PlotNumber, u.Price.Amount, u.Price.Currency, status.ToString()))
            : TypedResults.NotFound();

    private static async Task<Results<Ok<UnitResponseV2>, NotFound>> HandleV2(
        UnitId unitId, ICatalogueStore store, IUnitAvailability availability, CancellationToken cancellationToken) =>
        await FindAsync(unitId, store, availability, cancellationToken) is var (u, status) && u is not null
            ? TypedResults.Ok(new UnitResponseV2(u.Id.Value, u.DevelopmentId.Value, u.PlotNumber, new PriceResponse(u.Price.Amount, u.Price.Currency), status.ToString()))
            : TypedResults.NotFound();

    private static async Task<(Unit? Unit, UnitStatus Status)> FindAsync(
        UnitId unitId, ICatalogueStore store, IUnitAvailability availability, CancellationToken cancellationToken)
    {
        if (await store.FindUnitAsync(unitId, cancellationToken) is not { } unit)
        {
            return (null, default);
        }

        var statuses = await availability.StatusOfAsync([unitId], cancellationToken);
        return (unit, statuses.GetValueOrDefault(unitId));
    }
}
