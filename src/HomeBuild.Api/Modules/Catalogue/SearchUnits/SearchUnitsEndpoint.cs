using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace HomeBuild.Api.Modules.Catalogue.SearchUnits;

internal sealed record UnitSearchHit(Guid Id, Guid DevelopmentId, string Name, string PlotNumber, decimal Price, string Currency);

internal sealed record UnitSearchResult(IReadOnlyList<UnitSearchHit> Items);

internal static class SearchUnitsEndpoint
{
    private const int Limit = 20;

    public static void MapSearchUnits(this RouteGroupBuilder group) =>
        group.MapGet("/units/search", Handle)
            .WithName("SearchUnits")
            .WithSummary("Full-text search over unit names and descriptions (English stemming, web search syntax)");

    // Results are not yet scoped to a tenant: Lab 7 adds the tenant and enforces it in the database.
    private static async Task<Results<Ok<UnitSearchResult>, ValidationProblem>> Handle(
        string? q, CatalogueDbContext db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["q is required."] });
        }

        var hits = await db.Units.AsNoTracking()
            .Where(u => EF.Property<NpgsqlTsVector>(u, CatalogueDbContext.UnitSearchVector)
                .Matches(EF.Functions.WebSearchToTsQuery("english", q)))
            .OrderByDescending(u => EF.Property<NpgsqlTsVector>(u, CatalogueDbContext.UnitSearchVector)
                .Rank(EF.Functions.WebSearchToTsQuery("english", q)))
            .ThenBy(u => u.Id)
            .Take(Limit)
            .Select(u => new UnitSearchHit(u.Id.Value, u.DevelopmentId.Value, EF.Property<string>(u, CatalogueDbContext.UnitName),
                u.PlotNumber, u.Price.Amount, u.Price.Currency))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new UnitSearchResult(hits));
    }
}
