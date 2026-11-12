using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Sales.GetReservation;

internal static class GetReservationEndpoint
{
    public static void MapGetReservation(this RouteGroupBuilder group) =>
        group.MapGet("/reservations/{reservationId}", Handle)
            .WithName("GetReservation")
            .WithSummary("Get a reservation")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanReadReservations);

    // A buyer who does not own the reservation gets 404, the same as for a missing one, so IDs cannot be probed.
    private static async Task<Results<Ok<ReservationResponse>, NotFound>> Handle(
        ReservationId reservationId, IReservationStore reservations, IAuthorizationService authorization, HttpContext http,
        CancellationToken cancellationToken)
    {
        if (await reservations.FindReadOnlyAsync(reservationId, cancellationToken) is not { } r
            || !(await authorization.AuthorizeAsync(http.User, r, ReservationOperations.Read)).Succeeded)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(ReservationResponse.From(r));
    }
}
