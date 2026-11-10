using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Catalogue.GetDevelopmentPage;

internal sealed record PricePoint(decimal Amount, string Currency, DateTimeOffset EffectiveFrom);

internal sealed record LatestReservationView(Guid Id, string Status, DateTimeOffset CreatedAt);

internal sealed record DevelopmentPageUnit(
    Guid Id, string PlotNumber, decimal Price, string Currency, LatestReservationView? LatestReservation, IReadOnlyList<PricePoint> PriceHistory);

internal sealed record DevelopmentPageResponse(Guid Id, string Name, string Town, IReadOnlyList<DevelopmentPageUnit> Units);

internal static class GetDevelopmentPageEndpoint
{
    public static void MapGetDevelopmentPage(this RouteGroupBuilder group) =>
        group.MapGet("/developments/{developmentId}/page", Handle)
            .WithName("GetDevelopmentPage")
            .WithSummary("The development page: every unit with its latest reservation and price history");

    // Three queries whatever the size: the development, its units with price history (one projection, no tracking),
    // and Sales' latest reservation per unit in one batch. The first version issued 2 + 2 × units queries (502 for 250 units).
    private static async Task<Results<Ok<DevelopmentPageResponse>, NotFound>> Handle(
        DevelopmentId developmentId, CatalogueDbContext db, IUnitAvailability availability, CancellationToken cancellationToken)
    {
        var development = await db.Developments.AsNoTracking()
            .Where(d => d.Id == developmentId)
            .Select(d => new { d.Id, d.Name, d.Town })
            .SingleOrDefaultAsync(cancellationToken);
        if (development is null)
        {
            return TypedResults.NotFound();
        }

        var units = await db.Units.AsNoTracking()
            .Where(u => u.DevelopmentId == developmentId)
            .OrderBy(u => u.PlotNumber)
            .Select(u => new
            {
                u.Id,
                u.PlotNumber,
                u.Price.Amount,
                u.Price.Currency,
                History = db.UnitPrices.Where(p => p.UnitId == u.Id).OrderBy(p => p.EffectiveFrom)
                    .Select(p => new PricePoint(p.Amount, p.Currency, p.EffectiveFrom)).ToList(),
            })
            .ToListAsync(cancellationToken);

        var latest = await availability.LatestReservationsAsync([.. units.Select(u => u.Id)], cancellationToken);
        var rows = units.Select(u => new DevelopmentPageUnit(
            u.Id.Value, u.PlotNumber, u.Amount, u.Currency,
            latest.TryGetValue(u.Id, out var r) ? new LatestReservationView(r.ReservationId.Value, r.Status, r.CreatedAt) : null,
            u.History)).ToList();
        return TypedResults.Ok(new DevelopmentPageResponse(development.Id.Value, development.Name, development.Town, rows));
    }
}
