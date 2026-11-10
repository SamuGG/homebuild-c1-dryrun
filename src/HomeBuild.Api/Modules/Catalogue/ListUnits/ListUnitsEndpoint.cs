using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue.ListUnits;

internal sealed record UnitSummary(Guid Id, string PlotNumber, decimal Price, string Currency, string Status);

internal static class ListUnitsEndpoint
{
    public static void MapListUnits(this RouteGroupBuilder group) =>
        group.MapGet("/developments/{developmentId}/units", Handle);

    private static IResult Handle(DevelopmentId developmentId, ICatalogueStore store) =>
        store.FindDevelopment(developmentId) is null
            ? Results.NotFound()
            : Results.Ok(store.UnitsIn(developmentId).Select(u =>
                new UnitSummary(u.Id.Value, u.PlotNumber, u.Price.Amount, u.Price.Currency, u.Status.ToString())));
}
