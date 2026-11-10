using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Catalogue;
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

    private static Results<NoContent, NotFound, Conflict<ProblemDetails>> Handle(
        ReservationId reservationId, IReservationStore reservations, ICatalogueQueries catalogue, TimeProvider time, HttpContext http)
    {
        if (reservations.Find(reservationId) is not { } reservation)
        {
            return TypedResults.NotFound();
        }

        if (reservation.State is Cancelled)
        {
            return TypedResults.NoContent(); // DELETE is idempotent: already cancelled is success.
        }

        return reservation.Cancel(time.GetUtcNow()) switch
        {
            Transitioned => Release(catalogue, reservation.UnitId),
            InvalidTransition invalid => TypedResults.Conflict(Problems.Business(
                http, "reservation-not-cancellable", "Reservation cannot be cancelled", StatusCodes.Status409Conflict, invalid.Reason)),
        };
    }

    private static NoContent Release(ICatalogueQueries catalogue, UnitId unitId)
    {
        catalogue.FindUnit(unitId)?.Release();
        return TypedResults.NoContent();
    }
}
