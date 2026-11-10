using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace HomeBuild.Api.Modules.Sales.CancelReservation;

internal static class CancelReservationEndpoint
{
    public static void MapCancelReservation(this RouteGroupBuilder group) =>
        group.MapDelete("/reservations/{reservationId}", Handle)
            .WithName("CancelReservation")
            .WithSummary("Cancel a reservation (idempotent)");

    private static async Task<Results<NoContent, NotFound, Conflict<ProblemDetails>>> Handle(
        ReservationId reservationId, IReservationStore reservations, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (await reservations.FindAsync(reservationId, cancellationToken) is not { } reservation)
        {
            return TypedResults.NotFound();
        }

        if (reservation.State is Cancelled)
        {
            return TypedResults.NoContent(); // DELETE is idempotent: already cancelled is success.
        }

        return reservation.Cancel(time.GetUtcNow()) switch
        {
            Transitioned => await ReleaseAsync(reservations, reservation.UnitId, cancellationToken),
            InvalidTransition invalid => TypedResults.Conflict(Problems.Business(
                http, "reservation-not-cancellable", "Reservation cannot be cancelled", StatusCodes.Status409Conflict, invalid.Reason)),
        };
    }

    private static async Task<NoContent> ReleaseAsync(IReservationStore reservations, UnitId unitId, CancellationToken cancellationToken)
    {
        (await reservations.FindUnitAsync(unitId, cancellationToken))?.Release();
        await reservations.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
