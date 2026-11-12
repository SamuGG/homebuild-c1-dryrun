using System.ComponentModel.DataAnnotations;
using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Catalogue.EditDevelopment;

internal sealed record EditDevelopmentRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: Required, StringLength(100, MinimumLength = 1)] string Town);

internal static class EditDevelopmentEndpoint
{
    public static void MapEditDevelopment(this RouteGroupBuilder group) =>
        group.MapPut("/developments/{developmentId}", Handle)
            .WithName("EditDevelopment")
            .WithSummary("Edit a development's name and town (admin)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanManageCatalogue);

    private static async Task<Results<NoContent, NotFound>> Handle(
        DevelopmentId developmentId, EditDevelopmentRequest request, CatalogueDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Developments.SingleOrDefaultAsync(d => d.Id == developmentId, cancellationToken) is not { } development)
        {
            return TypedResults.NotFound();
        }

        var entry = db.Entry(development);
        entry.Property(d => d.Name).CurrentValue = request.Name;
        entry.Property(d => d.Town).CurrentValue = request.Town;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
