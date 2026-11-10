using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Catalogue.ListDevelopments;

internal sealed record DevelopmentSummary(Guid Id, string Name, string Town);

internal sealed record DevelopmentPage(IReadOnlyList<DevelopmentSummary> Items, int Page, int Size, int Total);

internal static class ListDevelopmentsEndpoint
{
    private const int MaxSize = 100;

    public static void MapListDevelopments(this RouteGroupBuilder group) =>
        group.MapGet("/developments", Handle)
            .WithName("ListDevelopments")
            .WithSummary("List developments, one page at a time");

    private static async Task<Results<Ok<DevelopmentPage>, ValidationProblem>> Handle(ICatalogueStore store, CancellationToken cancellationToken, int page = 1, int size = 20)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["page must be 1 or more."];
        }

        if (size is < 1 or > MaxSize)
        {
            errors["size"] = [$"size must be between 1 and {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var all = await store.DevelopmentsAsync(cancellationToken);
        var items = all.Skip((page - 1) * size).Take(size).Select(d => new DevelopmentSummary(d.Id.Value, d.Name, d.Town)).ToList();
        return TypedResults.Ok(new DevelopmentPage(items, page, size, all.Count));
    }
}
