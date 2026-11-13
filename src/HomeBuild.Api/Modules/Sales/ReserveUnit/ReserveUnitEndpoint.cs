using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace HomeBuild.Api.Modules.Sales.ReserveUnit;

/// <param name="BuyerId">Who the reservation is for. A buyer may omit it (themselves); a sales agent must name the buyer.</param>
internal sealed record ReserveRequest(
    Guid? BuyerId,
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
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireRateLimiting(RateLimiting.Reservations)
            .RequireAuthorization(Policies.CanReserve);

    internal static async Task<Results<Created<ReservationResponse>, NotFound, Conflict<ProblemDetails>, UnprocessableEntity<ProblemDetails>, ProblemHttpResult>>
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
        // Buyers reserve for themselves; only a sales agent may reserve for a named buyer.
        var forOthers = http.User.IsInRole(Roles.SalesAgent);
        var self = http.User.CallerBuyerId();
        BuyerId buyer;
        if (forOthers && request.BuyerId is { } named)
        {
            buyer = new BuyerId(named);
        }
        else if (!forOthers && self is { } me && (request.BuyerId is null || request.BuyerId == me.Value))
        {
            buyer = me;
        }
        else
        {
            return TypedResults.Problem(Problems.Business(http, "reserve-for-others", "Not allowed to reserve for this buyer",
                StatusCodes.Status403Forbidden,
                forOthers ? "A sales agent must name the buyer." : "Buyers can only reserve for themselves."));
        }

        if (await reservations.FindUnitAsync(unitId, cancellationToken) is not { } unit)
        {
            return TypedResults.NotFound();
        }

        // The domain refuses a deposit in another currency by throwing; answer it as the rule it is, not as a 500.
        if (request.Currency != unit.Price.Currency)
        {
            return TypedResults.UnprocessableEntity(Problems.Business(
                http, "deposit-currency", "Deposit currency rejected", StatusCodes.Status422UnprocessableEntity,
                $"The deposit must be in {unit.Price.Currency}, the unit's currency."));
        }

        var strategy = await catalogue.DepositStrategyForAsync(unit.DevelopmentId, cancellationToken);
        var unitPolicy = deposits.PolicyFor(unit, strategy, policy);
        // Saving can still lose the race: another buyer's write turns Reserved into UnitUnavailable.
        var result = await reservations.ReserveAndSaveAsync(
            unit, buyer, new Money(request.Deposit, request.Currency), unitPolicy, time.GetUtcNow(), cancellationToken);
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
