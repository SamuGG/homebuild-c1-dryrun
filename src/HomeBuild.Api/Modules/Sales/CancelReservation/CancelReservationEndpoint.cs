using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales.CancelReservation;

internal static class CancelReservationEndpoint
{
    public static void MapCancelReservation(this RouteGroupBuilder group) =>
        group.MapDelete("/reservations/{reservationId}", Handle);

    // Lab 1 only wires the slice; Lab 2 adds the cancel transition.
    private static IResult Handle(ReservationId reservationId, IReservationStore reservations) =>
        reservations.Find(reservationId) is null ? Results.NotFound() : Results.NoContent();
}
