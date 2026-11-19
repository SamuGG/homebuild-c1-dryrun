using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace HomeBuild.Api.Modules.Sales.CancelReservation;

internal static class CancelReservationEndpoint
{
    public static void MapCancelReservation(this RouteGroupBuilder group) =>
        group.MapDelete("/reservations/{reservationId}", Handle)
            .WithName("CancelReservation")
            .WithSummary("Cancel a reservation (idempotent)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanCancelReservations);

    private static async Task<Results<NoContent, NotFound, Conflict<ProblemDetails>, ProblemHttpResult>> Handle(
        ReservationId reservationId, IReservationStore reservations, IAuthorizationService authorization, IReservationExpirySchedules expiry,
        TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (await reservations.FindAsync(reservationId, cancellationToken) is not { } reservation
            || !(await authorization.AuthorizeAsync(http.User, reservation, ReservationOperations.Cancel)).Succeeded)
        {
            return TypedResults.NotFound(); // not theirs: as if it did not exist
        }

        var staff = http.User.IsInRole(Roles.SalesAgent) || http.User.IsInRole(Roles.Admin);
        if (!staff && reservation.State is Confirmed)
        {
            return TypedResults.Problem(Problems.Business(http, "deposit-paid", "Deposit already paid",
                StatusCodes.Status403Forbidden, "Once the deposit is paid, ask a sales agent to cancel."));
        }

        if (reservation.State is Cancelled)
        {
            return TypedResults.NoContent(); // DELETE is idempotent: already cancelled is success.
        }

        return reservation.Cancel(time.GetUtcNow()) switch
        {
            Transitioned => await ReleaseAsync(reservations, expiry, reservation.Id, reservation.UnitId, cancellationToken),
            InvalidTransition invalid => TypedResults.Conflict(Problems.Business(
                http, "reservation-not-cancellable", "Reservation cannot be cancelled", StatusCodes.Status409Conflict, invalid.Reason)),
        };
    }

    private static async Task<NoContent> ReleaseAsync(IReservationStore reservations, IReservationExpirySchedules expiry, ReservationId id, UnitId unitId, CancellationToken cancellationToken)
    {
        (await reservations.FindUnitAsync(unitId, cancellationToken))?.Release();
        await reservations.SaveChangesAsync(cancellationToken);
        await expiry.TryCancelAsync(id, cancellationToken); // C9 Lab 6: after the commit; best effort
        return TypedResults.NoContent();
    }
}
