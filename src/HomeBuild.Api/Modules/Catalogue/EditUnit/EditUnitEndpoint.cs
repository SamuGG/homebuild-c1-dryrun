using System.ComponentModel.DataAnnotations;
using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Catalogue.EditUnit;

/// <summary>The fields staff may edit. Status is not here: it follows the unit's reservations (Sales).</summary>
internal sealed record EditUnitRequest(
    [property: Range(1, 100_000_000)] decimal Price,
    [property: Required(AllowEmptyStrings = true), StringLength(2000)] string Description);

internal static class EditUnitEndpoint
{
    public static void MapEditUnit(this RouteGroupBuilder group) =>
        group.MapPut("/units/{unitId}", Handle)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("EditUnitV2")
            .WithSummary("Edit a unit's price and description (admin, v2)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanManageCatalogue);

    private static async Task<Results<NoContent, NotFound>> Handle(
        UnitId unitId, EditUnitRequest request, CatalogueDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await db.Units.SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken) is not { } unit)
        {
            return TypedResults.NotFound();
        }

        if (request.Price != unit.Price.Amount)
        {
            // C7 Lab 6: keep the price history C3 created. The first change also records the price it replaces.
            if (!await db.UnitPrices.AnyAsync(p => p.UnitId == unitId, cancellationToken))
            {
                db.UnitPrices.Add(new UnitPrice(Guid.CreateVersion7(), unit.Id, unit.DevelopmentId, unit.Price.Amount, unit.Price.Currency, DateTimeOffset.UnixEpoch));
            }

            db.UnitPrices.Add(new UnitPrice(Guid.CreateVersion7(), unit.Id, unit.DevelopmentId, request.Price, unit.Price.Currency, time.GetUtcNow()));
        }

        unit.ChangePrice(new Money(request.Price, unit.Price.Currency));
        db.Entry(unit).Property<string>(CatalogueDbContext.UnitDescription).CurrentValue = request.Description;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
