using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue.GetDevelopment;

internal sealed record DevelopmentResponse(Guid Id, string Name, string Town);

internal static class GetDevelopmentEndpoint
{
    public static void MapGetDevelopment(this RouteGroupBuilder group) =>
        group.MapGet("/developments/{developmentId}", Handle);

    private static IResult Handle(DevelopmentId developmentId, ICatalogueStore store) =>
        store.FindDevelopment(developmentId) is { } d
            ? Results.Ok(new DevelopmentResponse(d.Id.Value, d.Name, d.Town))
            : Results.NotFound();
}
