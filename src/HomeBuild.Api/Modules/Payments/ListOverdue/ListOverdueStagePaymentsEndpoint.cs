using HomeBuild.Api.Http;
using HomeBuild.Payments;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Payments.ListOverdue;

internal sealed record OverdueStagePaymentResponse(Guid UnitId, string Milestone, decimal Amount, string Currency, DateOnly DueFrom, int DaysOverdue);

/// <summary>Driving adapter: HTTP in, the core's use case, DTOs out. The rule (14 days to pay) lives in the core.</summary>
internal static class ListOverdueStagePaymentsEndpoint
{
    public static void MapListOverdueStagePayments(this RouteGroupBuilder group) =>
        group.MapGet("/stage-payments/overdue", Handle)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("ListOverdueStagePaymentsV2")
            .WithSummary("Stage payments unpaid more than 14 days after they fell due, most overdue first (finance, v2)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanReviewStagePayments);

    private static async Task<Ok<List<OverdueStagePaymentResponse>>> Handle(
        ListOverdueStagePayments useCase, TimeProvider time, CancellationToken cancellationToken) =>
        TypedResults.Ok((await useCase.HandleAsync(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime), cancellationToken))
            .Select(p => new OverdueStagePaymentResponse(p.UnitId.Value, p.Milestone.ToString(), p.Amount.Amount, p.Amount.Currency, p.DueFrom, p.DaysOverdue))
            .ToList());
}
