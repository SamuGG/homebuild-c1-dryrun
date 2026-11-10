using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Sales.GetReservation;

internal static class GetReservationEndpoint
{
    public static void MapGetReservation(this RouteGroupBuilder group) =>
        group.MapGet("/reservations/{reservationId}", Handle)
            .WithName("GetReservation")
            .WithSummary("Get a reservation");

    private static async Task<Results<Ok<ReservationResponse>, NotFound>> Handle(
        ReservationId reservationId, IReservationStore reservations, CancellationToken cancellationToken) =>
        await reservations.FindReadOnlyAsync(reservationId, cancellationToken) is { } r ? TypedResults.Ok(ReservationResponse.From(r)) : TypedResults.NotFound();
}
