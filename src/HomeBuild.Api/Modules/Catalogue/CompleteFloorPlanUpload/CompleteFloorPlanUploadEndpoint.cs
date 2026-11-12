using System.ComponentModel.DataAnnotations;
using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Catalogue.CompleteFloorPlanUpload;

internal sealed record CompleteUploadRequest([property: Required, StringLength(200)] string Key);

internal sealed record FloorPlanResponse(string Key);

internal static class CompleteFloorPlanUploadEndpoint
{
    public static void MapCompleteFloorPlanUpload(this RouteGroupBuilder group) =>
        group.MapPost("/units/{unitId}/floor-plan", Handle)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("CompleteFloorPlanUploadV2")
            .WithSummary("Check an uploaded floor plan and move it into place (admin, v2)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(Policies.CanManageCatalogue);

    private static async Task<Results<Ok<FloorPlanResponse>, NotFound, ProblemHttpResult>> Handle(
        UnitId unitId, CompleteUploadRequest request, ICatalogueStore store, ITenantContext tenant, FloorPlanStorage storage,
        HttpContext http, CancellationToken cancellationToken)
    {
        // The key comes from the browser: check it belongs to this tenant and unit before anything touches S3.
        if (!FloorPlanStorage.IsOwnIncomingKey(request.Key, tenant.TenantId!.Value, unitId))
        {
            return TypedResults.Problem(Problems.Business(http, "foreign-upload-key", "Not your upload", StatusCodes.Status403Forbidden,
                "The key is not an upload for this unit in your catalogue."));
        }

        if (!storage.IsConfigured)
        {
            return TypedResults.Problem(Problems.Business(http, "storage-unavailable", "Uploads are not configured", StatusCodes.Status503ServiceUnavailable));
        }

        if (await store.FindUnitAsync(unitId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        return await storage.CheckAndMoveAsync(request.Key, cancellationToken) switch
        {
            Moved m => TypedResults.Ok(new FloorPlanResponse(m.Key)),
            UploadMissing => TypedResults.NotFound(),
            UploadRejected r => TypedResults.Problem(Problems.Business(http, "floor-plan-rejected", "Floor plan rejected", StatusCodes.Status422UnprocessableEntity, r.Reason)),
            _ => throw new InvalidOperationException("Unknown move result."),
        };
    }
}
