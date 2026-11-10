using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue.GetUnit;

internal sealed record UnitResponse(Guid Id, Guid DevelopmentId, string PlotNumber, decimal Price, string Currency, string Status);

internal static class GetUnitEndpoint
{
    public static void MapGetUnit(this RouteGroupBuilder group) =>
        group.MapGet("/units/{unitId}", Handle);

    private static IResult Handle(UnitId unitId, ICatalogueStore store) =>
        store.FindUnit(unitId) is { } u
            ? Results.Ok(new UnitResponse(u.Id.Value, u.DevelopmentId.Value, u.PlotNumber, u.Price.Amount, u.Price.Currency, u.Status.ToString()))
            : Results.NotFound();
}
