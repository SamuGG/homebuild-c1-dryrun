using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Catalogue.ListUnits;

internal sealed record UnitSummary(Guid Id, string PlotNumber, decimal Price, string Currency, string Status);

/// <summary>v2: one keyset page. <c>Next</c> is an opaque cursor, null on the last page; there is no total count.</summary>
internal sealed record UnitPage(IReadOnlyList<UnitSummary> Items, string? Next);

internal static class ListUnitsEndpoint
{
    private const int MaxSize = 100;

    public static void MapListUnits(this RouteGroupBuilder group)
    {
        group.MapGet("/developments/{developmentId}/units", Handle)
            .MapToApiVersion(ApiVersions.V1)
            .WithName("ListUnits")
            .WithSummary("List a development's units, optionally by status (v1: every unit, unpaged)");
        group.MapGet("/developments/{developmentId}/units", HandleV2)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("ListUnitsV2")
            .WithSummary("List a development's units by price, one keyset page at a time (v2)");
    }

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

    // Keyset paging on (price, unit id): "the next rows after this key", answered by ix_units_development_price_id
    // and stable while units are inserted. The status filter applies within each page, so a filtered page can be short.
    private static async Task<Results<Ok<UnitPage>, NotFound, ValidationProblem>> HandleV2(
        DevelopmentId developmentId,
        CatalogueDbContext db,
        UnitsCursor cursors,
        IUnitAvailability availability,
        CancellationToken cancellationToken,
        string? after = null,
        int size = 20,
        UnitStatus? status = null)
    {
        if (size is < 1 or > MaxSize)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["size"] = [$"size must be between 1 and {MaxSize}."] });
        }

        decimal afterPrice = default;
        UnitId afterId = default;
        if (after is not null && !cursors.TryDecode(after, out afterPrice, out afterId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["after"] = ["The cursor is not valid."] });
        }

        if (!await db.Developments.AnyAsync(d => d.Id == developmentId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var query = db.Units.AsNoTracking().Where(u => u.DevelopmentId == developmentId);
        if (after is not null)
        {
            query = query.Where(u => EF.Functions.GreaterThan(
                ValueTuple.Create(u.Price.Amount, u.Id), ValueTuple.Create(afterPrice, afterId)));
        }

        var page = await query.OrderBy(u => u.Price.Amount).ThenBy(u => u.Id).Take(size + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > size;
        if (hasMore)
        {
            page.RemoveAt(size);
        }

        var statuses = await availability.StatusOfAsync([.. page.Select(u => u.Id)], cancellationToken);
        var items = page
            .Select(u => new UnitSummary(u.Id.Value, u.PlotNumber, u.Price.Amount, u.Price.Currency, statuses.GetValueOrDefault(u.Id).ToString()))
            .Where(s => status is null || s.Status == status.ToString())
            .ToList();
        var next = hasMore ? cursors.Encode(page[^1].Price.Amount, page[^1].Id) : null;
        return TypedResults.Ok(new UnitPage(items, next));
    }
}
