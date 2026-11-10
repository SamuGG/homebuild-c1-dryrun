using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Catalogue.GetDevelopment;

internal sealed record DevelopmentResponse(Guid Id, string Name, string Town);

internal static class GetDevelopmentEndpoint
{
    public static void MapGetDevelopment(this RouteGroupBuilder group) =>
        group.MapGet("/developments/{developmentId}", Handle)
            .WithName("GetDevelopment")
            .WithSummary("Get a development");

    private static async Task<Results<Ok<DevelopmentResponse>, NotFound>> Handle(
        DevelopmentId developmentId, ICatalogueStore store, CancellationToken cancellationToken) =>
        await store.FindDevelopmentAsync(developmentId, cancellationToken) is { } d
            ? TypedResults.Ok(new DevelopmentResponse(d.Id.Value, d.Name, d.Town))
            : TypedResults.NotFound();
}
