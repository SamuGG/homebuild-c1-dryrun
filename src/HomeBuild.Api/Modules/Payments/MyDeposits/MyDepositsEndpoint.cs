using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Payments.MyDeposits;

/// <param name="Status">paid, refund-pending or refunded.</param>
/// <param name="Reason">Why the deposit is being or was given back, in words for the buyer; null when kept.</param>
internal sealed record MyDepositResponse(Guid ReservationId, decimal Amount, string Currency, DateTimeOffset PaidAt, string Status, string? Reason, DateTimeOffset? RefundedAt);

/// <summary>
/// C8 Lab 5: GET /me/deposits. The signed-in buyer's deposits and what happened to each, so My Home can say why a late
/// payment was refunded. The buyer's reservations come from Sales' published read; the deposits are Payments' own.
/// </summary>
internal static class MyDepositsEndpoint
{
    public static void MapMyDeposits(this RouteGroupBuilder group) =>
        group.MapGet("/me/deposits", Handle)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("ListMyDeposits")
            .WithSummary("The signed-in buyer's deposits: paid, refund pending or refunded, with the reason (v2)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanReadOwnProgress);

    private static async Task<Results<Ok<List<MyDepositResponse>>, ForbidHttpResult>> Handle(
        IUnitAvailability sales, PaymentsDbContext db, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer)
        {
            return TypedResults.Forbid();
        }

        var reservations = (await sales.ReservationsOfAsync(buyer, cancellationToken)).Select(r => r.ReservationId).ToList();
        var deposits = await db.Deposits.AsNoTracking().Where(d => reservations.Contains(d.ReservationId)).OrderByDescending(d => d.PaidAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(deposits.Select(d => new MyDepositResponse(
            d.ReservationId.Value, d.Amount, d.Currency, d.PaidAt,
            d.RefundReference is not null ? "refunded" : d.RefundReason is not null ? "refund-pending" : "paid",
            d.RefundReason, d.RefundedAt)).ToList());
    }
}
