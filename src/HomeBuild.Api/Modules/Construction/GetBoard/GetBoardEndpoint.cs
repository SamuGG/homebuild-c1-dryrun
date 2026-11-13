using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Construction.GetBoard;

/// <summary>Staff view: every milestone with its internal fields (subcontractor, site notes, open defects).</summary>
internal sealed record BoardResponse(Guid DevelopmentId, string DevelopmentName, IReadOnlyList<string> Milestones, IReadOnlyList<BoardRow> Rows);

internal sealed record BoardRow(Guid UnitId, string PlotNumber, IReadOnlyList<BoardCell> Cells);

internal sealed record BoardCell(string Milestone, string Status, DateOnly? CompletedOn, string Subcontractor, string SiteNotes, int OpenDefects);

internal static class GetBoardEndpoint
{
    public static void MapGetBoard(this RouteGroupBuilder group) =>
        group.MapGet("/developments/{developmentId}/board", Handle)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("GetBuildBoardV2")
            .WithSummary("A development's build milestones per unit, with internal fields (site staff, v2)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanViewBuildBoard);

    private static async Task<Results<Ok<BoardResponse>, NotFound>> Handle(
        DevelopmentId developmentId, ICatalogueQueries catalogue, BuildProgressReader builds, CancellationToken cancellationToken)
    {
        if (await catalogue.PlotsInAsync(developmentId, cancellationToken) is not { } plots)
        {
            return TypedResults.NotFound();
        }

        var name = plots.Count > 0 ? plots[0].DevelopmentName : "";
        var progress = await builds.ForAsync([.. plots.Select(p => (p.UnitId, p.DevelopmentId))], cancellationToken);
        var rows = plots.Select(p => new BoardRow(p.UnitId.Value, p.PlotNumber, [.. progress[p.UnitId].Milestones
            .Select(m => new BoardCell(BuildProgressReader.DisplayName(m.Milestone), BuildProgressReader.BoardStatus(m.Status), m.CompletedOn, m.Subcontractor, m.SiteNotes, m.OpenDefects))])).ToList();
        return TypedResults.Ok(new BoardResponse(developmentId.Value, name, [.. BuildProgress.Order.Select(BuildProgressReader.DisplayName)], rows));
    }
}
