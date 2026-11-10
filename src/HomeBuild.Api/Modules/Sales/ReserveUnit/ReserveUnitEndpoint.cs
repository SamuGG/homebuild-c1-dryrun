using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales.ReserveUnit;

internal sealed record ReserveRequest(Guid BuyerId, decimal Deposit, string Currency);

internal static class ReserveUnitEndpoint
{
    public static void MapReserveUnit(this RouteGroupBuilder group) =>
        group.MapPost("/units/{unitId}/reservations", Handle);

    private static IResult Handle(
        UnitId unitId,
        ReserveRequest request,
        ICatalogueQueries catalogue,
        IReservationStore reservations,
        ReservationPolicy policy,
        TimeProvider time,
        ILogger<ReserveRequest> logger)
    {
        if (catalogue.FindUnit(unitId) is not { } unit)
        {
            return Results.NotFound();
        }

        var result = unit.Reserve(new BuyerId(request.BuyerId), new Money(request.Deposit, request.Currency), policy, time.GetUtcNow());
        logger.LogOutcome(unitId, result);
        if (result is Reserved reserved)
        {
            reservations.Add(reserved.Reservation);
            return Results.Created($"/reservations/{reserved.Reservation.Id}", ReservationResponse.From(reserved.Reservation));
        }

        return Results.Conflict();
    }
}
