using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales.GetReservation;

internal static class GetReservationEndpoint
{
    public static void MapGetReservation(this RouteGroupBuilder group) =>
        group.MapGet("/reservations/{reservationId}", Handle);

    private static IResult Handle(ReservationId reservationId, IReservationStore reservations) =>
        reservations.Find(reservationId) is { } r ? Results.Ok(ReservationResponse.From(r)) : Results.NotFound();
}
