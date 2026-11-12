using Microsoft.AspNetCore.Antiforgery;

namespace HomeBuild.Web.Api;

/// <summary>
/// The browser's two calls around a direct-to-S3 upload, relayed to the API with the user's token. The web app has no
/// AWS credentials: the API signs, S3 receives the bytes, and this server only passes small JSON messages.
/// </summary>
public static class FloorPlanEndpoints
{
    private sealed record CompleteRequest(string Key);

    public static IEndpointRouteBuilder MapFloorPlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/staff/units/{unitId:guid}/floor-plan")
            .RequireAuthorization(policy => policy.RequireRole("admin"))
            .AddEndpointFilter(async (context, next) =>
            {
                // JSON posts are not form posts, so check the antiforgery token (sent as a header) explicitly.
                try
                {
                    await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
                }
                catch (AntiforgeryValidationException)
                {
                    return Results.BadRequest();
                }

                return await next(context);
            });

        group.MapPost("/ticket", async (Guid unitId, HomeApiClient api, CancellationToken cancellationToken) =>
            await api.StartFloorPlanUploadAsync(unitId, cancellationToken) is { Succeeded: true } ticket
                ? Results.Json(ticket.Value)
                : Results.StatusCode(StatusCodes.Status502BadGateway));

        group.MapPost("/complete", async (Guid unitId, CompleteRequest request, HomeApiClient api, CancellationToken cancellationToken) =>
        {
            var result = await api.CompleteFloorPlanUploadAsync(unitId, request.Key, cancellationToken);
            return result.Succeeded
                ? Results.Json(new { key = result.Value })
                : Results.Problem(title: result.Problem!.Title, detail: result.Problem.Detail, statusCode: result.Problem.Status);
        });
        return endpoints;
    }
}
