using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Catalogue.GetUnit;

internal sealed record UnitResponse(Guid Id, Guid DevelopmentId, string PlotNumber, decimal Price, string Currency, string Status);

internal static class GetUnitEndpoint
{
    public static void MapGetUnit(this RouteGroupBuilder group) =>
        group.MapGet("/units/{unitId}", Handle);

    private static Results<Ok<UnitResponse>, NotFound> Handle(UnitId unitId, ICatalogueStore store) =>
        store.FindUnit(unitId) is { } u
            ? TypedResults.Ok(new UnitResponse(u.Id.Value, u.DevelopmentId.Value, u.PlotNumber, u.Price.Amount, u.Price.Currency, u.Status.ToString()))
            : TypedResults.NotFound();
}
