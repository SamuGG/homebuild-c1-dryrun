using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Catalogue.ListPriceChanges;

internal sealed record PriceChange(Guid UnitId, string PlotNumber, decimal PreviousPrice, decimal CurrentPrice, string Currency, DateTimeOffset ChangedAt);

/// <summary>A vertical slice (C7 Lab 6): request, query and response in one place, on the DbContext directly.</summary>
internal static class ListPriceChangesEndpoint
{
    public static void MapListPriceChanges(this RouteGroupBuilder group) =>
        group.MapGet("/developments/{developmentId}/price-changes", Handle)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("ListPriceChangesV2")
            .WithSummary("A development's units whose price has changed, latest change first (v2)");

    private static async Task<Results<Ok<List<PriceChange>>, NotFound>> Handle(
        DevelopmentId developmentId, CatalogueDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Developments.AnyAsync(d => d.Id == developmentId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var history = await db.UnitPrices.AsNoTracking().Where(p => p.DevelopmentId == developmentId).ToListAsync(cancellationToken);
        var plots = await db.Units.AsNoTracking().Where(u => u.DevelopmentId == developmentId).ToDictionaryAsync(u => u.Id, u => u.PlotNumber, cancellationToken);
        return TypedResults.Ok(history
            .GroupBy(p => p.UnitId)
            .Where(g => g.Count() > 1)
            .Select(g => g.OrderByDescending(p => p.EffectiveFrom).Take(2).ToList())
            .Select(last => new PriceChange(last[0].UnitId.Value, plots[last[0].UnitId], last[1].Amount, last[0].Amount, last[0].Currency, last[0].EffectiveFrom))
            .OrderByDescending(c => c.ChangedAt)
            .ToList());
    }
}
