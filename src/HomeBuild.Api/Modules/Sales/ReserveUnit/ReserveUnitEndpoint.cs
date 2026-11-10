using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace HomeBuild.Api.Modules.Sales.ReserveUnit;

internal sealed record ReserveRequest(
    Guid BuyerId,
    [property: Range(0.01, 100_000_000)] decimal Deposit,
    [property: Required, RegularExpression("^[A-Z]{3}$")] string Currency);

internal static class ReserveUnitEndpoint
{
    public static void MapReserveUnit(this RouteGroupBuilder group) =>
        group.MapPost("/units/{unitId}/reservations", Reserve)
            .WithName("ReserveUnit")
            .WithSummary("Reserve a unit with a deposit")
            .ProducesValidationProblem() // AddValidation and the default-ID filter answer before the handler
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(RateLimiting.Reservations);

    internal static async Task<Results<Created<ReservationResponse>, NotFound, Conflict<ProblemDetails>, UnprocessableEntity<ProblemDetails>>>
        Reserve(
            UnitId unitId,
            ReserveRequest request,
            ICatalogueQueries catalogue,
            IReservationStore reservations,
            ReservationPolicy policy,
            DepositStrategySelector deposits,
            TimeProvider time,
            ILogger<ReserveRequest> logger,
            HttpContext http,
            CancellationToken cancellationToken)
    {
        if (await reservations.FindUnitAsync(unitId, cancellationToken) is not { } unit)
        {
            return TypedResults.NotFound();
        }

        var strategy = await catalogue.DepositStrategyForAsync(unit.DevelopmentId, cancellationToken);
        var unitPolicy = deposits.PolicyFor(unit, strategy, policy);
        // Saving can still lose the race: another buyer's write turns Reserved into UnitUnavailable.
        var result = await reservations.ReserveAndSaveAsync(
            unit, new BuyerId(request.BuyerId), new Money(request.Deposit, request.Currency), unitPolicy, time.GetUtcNow(), cancellationToken);
        logger.LogOutcome(unitId, result);

        // No default arm: a new ReserveResult case must fail the build here.
        return result switch
        {
            Reserved r => Created(r.Reservation, http),
            UnitUnavailable u => TypedResults.Conflict(Problems.Business(
                http, "unit-unavailable", "Unit unavailable", StatusCodes.Status409Conflict, $"Unit {u.UnitId} is {u.Status}.")),
            DepositRejected d => TypedResults.UnprocessableEntity(Problems.Business(
                http, "deposit-rejected", "Deposit rejected", StatusCodes.Status422UnprocessableEntity, $"Deposit {d.Offered.Amount} {d.Offered.Currency} is outside the allowed range (minimum {d.Minimum.Amount} {d.Minimum.Currency}).")),
        };
    }

    private static Created<ReservationResponse> Created(Reservation reservation, HttpContext http)
    {
        var version = http.RequestedApiVersion?.MajorVersion ?? 1;
        return TypedResults.Created($"/v{version}/reservations/{reservation.Id}", ReservationResponse.From(reservation));
    }
}
