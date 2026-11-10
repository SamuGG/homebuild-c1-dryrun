using HomeBuild.Api.Modules.Sales;
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

    private static async Task<Results<Ok<List<UnitSummary>>, NotFound>> Handle(
        DevelopmentId developmentId, ICatalogueStore store, IUnitAvailability availability, CancellationToken cancellationToken, UnitStatus? status = null)
    {
        if (await store.FindDevelopmentAsync(developmentId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var units = await store.UnitsInAsync(developmentId, cancellationToken);
        var statuses = await availability.StatusOfAsync([.. units.Select(u => u.Id)], cancellationToken);
        return TypedResults.Ok(units
            .Select(u => (Unit: u, Status: statuses.GetValueOrDefault(u.Id)))
            .Where(x => status is null || x.Status == status)
            .Select(x => new UnitSummary(x.Unit.Id.Value, x.Unit.PlotNumber, x.Unit.Price.Amount, x.Unit.Price.Currency, x.Status.ToString()))
            .ToList());
    }
}
