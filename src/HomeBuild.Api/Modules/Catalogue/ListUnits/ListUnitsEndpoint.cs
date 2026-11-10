using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Catalogue.ListUnits;

internal sealed record UnitSummary(Guid Id, string PlotNumber, decimal Price, string Currency, string Status);

internal static class ListUnitsEndpoint
{
    public static void MapListUnits(this RouteGroupBuilder group) =>
        group.MapGet("/developments/{developmentId}/units", Handle)
            .WithName("ListUnits")
            .WithSummary("List a development's units, optionally by status");

    private static Results<Ok<List<UnitSummary>>, NotFound> Handle(DevelopmentId developmentId, ICatalogueStore store, UnitStatus? status = null)
    {
        if (store.FindDevelopment(developmentId) is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(store.UnitsIn(developmentId)
            .Where(u => status is null || u.Status == status)
            .Select(u => new UnitSummary(u.Id.Value, u.PlotNumber, u.Price.Amount, u.Price.Currency, u.Status.ToString()))
            .ToList());
    }
}
