using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Sales.GetReservation;

internal static class GetReservationEndpoint
{
    public static void MapGetReservation(this RouteGroupBuilder group) =>
        group.MapGet("/reservations/{reservationId}", Handle);

    private static Results<Ok<ReservationResponse>, NotFound> Handle(ReservationId reservationId, IReservationStore reservations) =>
        reservations.Find(reservationId) is { } r ? TypedResults.Ok(ReservationResponse.From(r)) : TypedResults.NotFound();
}
