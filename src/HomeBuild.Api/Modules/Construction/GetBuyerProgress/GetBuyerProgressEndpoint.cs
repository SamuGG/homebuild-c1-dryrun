using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Construction.GetBuyerProgress;

/// <summary>
/// Buyer view: a separate DTO built field by field, so internal fields (subcontractors, site notes, defects, other
/// buyers) can never reach it by accident. It holds only the caller's own homes.
/// </summary>
internal sealed record BuyerProgressResponse(IReadOnlyList<BuyerHome> Homes);

internal sealed record BuyerHome(
    Guid ReservationId, Guid UnitId, string PlotNumber, string DevelopmentName, string ReservationState, DateTimeOffset ReservationExpiresAt, BuyerProgress Progress);

/// <summary>Simplified progress: the stage reached, how many of the steps are done, and what comes next.</summary>
internal sealed record BuyerProgress(string Stage, int StepsDone, int StepsTotal, string? NextStep);

internal static class GetBuyerProgressEndpoint
{
    public static void MapGetBuyerProgress(this RouteGroupBuilder group) =>
        group.MapGet("/me/progress", Handle)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("GetMyProgressV2")
            .WithSummary("The signed-in buyer's homes and their simplified build progress (v2)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanReadOwnProgress);

    private static async Task<Ok<BuyerProgressResponse>> Handle(
        HttpContext http, IUnitAvailability sales, ICatalogueQueries catalogue, BuildProgressReader builds, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer)
        {
            return TypedResults.Ok(new BuyerProgressResponse([]));
        }

        var reservations = await sales.ReservationsOfAsync(buyer, cancellationToken);
        var plots = await catalogue.PlotsAsync([.. reservations.Select(r => r.UnitId).Distinct()], cancellationToken);
        var progressByUnit = await builds.ForAsync([.. plots.Values.Select(p => (p.UnitId, p.DevelopmentId))], cancellationToken);
        var homes = reservations
            .Where(r => plots.ContainsKey(r.UnitId))
            .Select(r =>
            {
                var steps = progressByUnit[r.UnitId].Milestones;
                var done = steps.Count(s => s.Status == MilestoneStatus.Completed);
                var current = steps.FirstOrDefault(s => s.Status != MilestoneStatus.Completed);
                var next = steps.SkipWhile(s => s.Status == MilestoneStatus.Completed).Skip(1).FirstOrDefault();
                var progress = new BuyerProgress(
                    current is null ? "Ready to move in" : BuildProgressReader.DisplayName(current.Milestone), done, steps.Count,
                    next is null ? null : BuildProgressReader.DisplayName(next.Milestone));
                var plot = plots[r.UnitId];
                return new BuyerHome(r.ReservationId.Value, r.UnitId.Value, plot.PlotNumber, plot.DevelopmentName, r.State, r.ExpiresAt, progress);
            })
            .ToList();
        return TypedResults.Ok(new BuyerProgressResponse(homes));
    }
}
