using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace HomeBuild.Api.Modules.Sales.ReserveUnit;

internal sealed record ReserveRequest(Guid BuyerId, decimal Deposit, string Currency);

internal static class ReserveUnitEndpoint
{
    public static void MapReserveUnit(this RouteGroupBuilder group) =>
        group.MapPost("/units/{unitId}/reservations", Reserve);

    internal static Results<Created<ReservationResponse>, NotFound, Conflict<ProblemDetails>, UnprocessableEntity<ProblemDetails>>
        Reserve(
            UnitId unitId,
            ReserveRequest request,
            ICatalogueQueries catalogue,
            IReservationStore reservations,
            ReservationPolicy policy,
            DepositStrategySelector deposits,
            TimeProvider time,
            ILogger<ReserveRequest> logger)
    {
        if (catalogue.FindUnit(unitId) is not { } unit)
        {
            return TypedResults.NotFound();
        }

        var unitPolicy = deposits.PolicyFor(unit, catalogue.DepositStrategyFor(unit.DevelopmentId), policy);
        var result = unit.Reserve(new BuyerId(request.BuyerId), new Money(request.Deposit, request.Currency), unitPolicy, time.GetUtcNow());
        logger.LogOutcome(unitId, result);

        // No default arm: a new ReserveResult case must fail the build here.
        return result switch
        {
            Reserved r => Created(reservations, r.Reservation),
            UnitUnavailable u => TypedResults.Conflict(new ProblemDetails
            {
                Title = "Unit unavailable",
                Detail = $"Unit {u.UnitId} is {u.Status}.",
                Status = StatusCodes.Status409Conflict,
            }),
            DepositRejected d => TypedResults.UnprocessableEntity(new ProblemDetails
            {
                Title = "Deposit rejected",
                Detail = $"Deposit {d.Offered.Amount} {d.Offered.Currency} is outside the allowed range (minimum {d.Minimum.Amount} {d.Minimum.Currency}).",
                Status = StatusCodes.Status422UnprocessableEntity,
            }),
        };
    }

    private static Created<ReservationResponse> Created(IReservationStore reservations, Reservation reservation)
    {
        reservations.Add(reservation);
        return TypedResults.Created($"/reservations/{reservation.Id}", ReservationResponse.From(reservation));
    }
}
