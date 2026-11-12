using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Catalogue.StartFloorPlanUpload;

internal sealed record UploadTicketResponse(Uri Url, IReadOnlyDictionary<string, string> Fields, string Key, long MaxBytes, string ContentType, DateTimeOffset ExpiresAt);

internal static class StartFloorPlanUploadEndpoint
{
    public static void MapStartFloorPlanUpload(this RouteGroupBuilder group) =>
        group.MapPost("/units/{unitId}/floor-plan/uploads", Handle)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("StartFloorPlanUploadV2")
            .WithSummary("A pre-signed POST for uploading a unit's floor plan (PDF, up to 10 MB, 5 minutes) straight to S3 (admin, v2)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(Policies.CanManageCatalogue);

    private static async Task<Results<Ok<UploadTicketResponse>, NotFound, ProblemHttpResult>> Handle(
        UnitId unitId, ICatalogueStore store, ITenantContext tenant, FloorPlanStorage storage, HttpContext http, CancellationToken cancellationToken)
    {
        if (!storage.IsConfigured)
        {
            return TypedResults.Problem(Problems.Business(http, "storage-unavailable", "Uploads are not configured", StatusCodes.Status503ServiceUnavailable));
        }

        if (await store.FindUnitAsync(unitId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var ticket = await storage.CreateUploadAsync(tenant.TenantId!.Value, unitId);
        return TypedResults.Ok(new UploadTicketResponse(ticket.Url, ticket.Fields, ticket.Key, ticket.MaxBytes, ticket.ContentType, ticket.ExpiresAt));
    }
}
