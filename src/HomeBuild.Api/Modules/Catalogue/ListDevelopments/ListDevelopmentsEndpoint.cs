namespace HomeBuild.Api.Modules.Catalogue.ListDevelopments;

internal sealed record DevelopmentSummary(Guid Id, string Name, string Town);

internal static class ListDevelopmentsEndpoint
{
    public static void MapListDevelopments(this RouteGroupBuilder group) =>
        group.MapGet("/developments", Handle);

    private static IResult Handle(ICatalogueStore store) =>
        Results.Ok(store.Developments().Select(d => new DevelopmentSummary(d.Id.Value, d.Name, d.Town)));
}
