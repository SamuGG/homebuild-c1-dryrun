using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Sales.GetDeposit;

internal sealed record DepositResponse(Guid ReservationId, decimal Amount, string Currency, bool Paid, DateTimeOffset? PaidAt);

internal static class GetDepositEndpoint
{
    public static void MapGetDeposit(this RouteGroupBuilder group) =>
        group.MapGet("/reservations/{reservationId}/deposit", Handle)
            .WithName("GetDeposit")
            .WithSummary("Read a reservation's deposit and payment (finance, admin, the owning buyer)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanReadPayments);

    private static async Task<Results<Ok<DepositResponse>, NotFound>> Handle(
        ReservationId reservationId, IReservationStore reservations, IAuthorizationService authorization, HttpContext http,
        CancellationToken cancellationToken)
    {
        if (await reservations.FindReadOnlyAsync(reservationId, cancellationToken) is not { } r
            || !(await authorization.AuthorizeAsync(http.User, r, ReservationOperations.ReadPayments)).Succeeded)
        {
            return TypedResults.NotFound();
        }

        var paidAt = r.State is Confirmed c ? c.DepositPaidAt : (DateTimeOffset?)null;
        return TypedResults.Ok(new DepositResponse(r.Id.Value, r.Deposit.Amount, r.Deposit.Currency, paidAt is not null, paidAt));
    }
}
