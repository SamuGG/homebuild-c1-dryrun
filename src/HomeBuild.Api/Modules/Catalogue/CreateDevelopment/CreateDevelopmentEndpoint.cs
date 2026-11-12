using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Catalogue.CreateDevelopment;

internal sealed record CreateDevelopmentRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: Required, StringLength(100, MinimumLength = 1)] string Town,
    [property: Required, RegularExpression("^(fixed|percentage)$")] string DepositStrategy);

internal sealed record CreatedDevelopment(Guid Id, string Name, string Town);

internal static class CreateDevelopmentEndpoint
{
    public static void MapCreateDevelopment(this RouteGroupBuilder group) =>
        group.MapPost("/developments", Handle)
            .WithName("CreateDevelopment")
            .WithSummary("Create a development (admin)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanManageCatalogue);

    private static async Task<Created<CreatedDevelopment>> Handle(
        CreateDevelopmentRequest request, CatalogueDbContext db, HttpContext http, CancellationToken cancellationToken)
    {
        var development = new Development(DevelopmentId.New(), request.Name, request.Town, request.DepositStrategy);
        db.Developments.Add(development);
        await db.SaveChangesAsync(cancellationToken); // stamped with the caller's tenant
        var version = http.RequestedApiVersion?.MajorVersion ?? 1;
        return TypedResults.Created($"/v{version}/developments/{development.Id}",
            new CreatedDevelopment(development.Id.Value, development.Name, development.Town));
    }
}
