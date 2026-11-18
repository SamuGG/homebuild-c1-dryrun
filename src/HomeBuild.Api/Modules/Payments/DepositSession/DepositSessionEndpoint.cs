using HomeBuild.Api.Configuration;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using HomeBuild.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Modules.Payments.DepositSession;

/// <summary>A Checkout page for a deposit: send the buyer to <see cref="Url"/> (it closes at <see cref="ExpiresAt"/>).</summary>
internal sealed record DepositSessionResponse(string SessionId, Uri Url, DateTimeOffset ExpiresAt);

/// <summary>
/// C8 Lab 1: POST /reservations/{id}/deposit-session. The buyer who owns a held reservation gets a Stripe Checkout
/// page for its deposit. Nothing here marks the reservation paid: only the signed webhook does that (Lab 2), never the
/// browser reaching the success URL, which anyone can open.
/// </summary>
internal static class DepositSessionEndpoint
{
    public static void MapDepositSession(this RouteGroupBuilder group) =>
        group.MapPost("/reservations/{reservationId}/deposit-session", Handle)
            .WithName("CreateDepositSession")
            .WithSummary("Open a Stripe Checkout page for a held reservation's deposit")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanPayDeposits);

    private static async Task<Results<Ok<DepositSessionResponse>, NotFound, Conflict<ProblemDetails>>> Handle(
        ReservationId reservationId, IReservationReader reservations, IAuthorizationService authorization, ITenantContext tenant,
        StartDepositCheckout checkout, IOptions<PaymentsOptions> options, HttpContext http, CancellationToken cancellationToken)
    {
        // Not the caller's reservation: 404, as for one that does not exist (C4), so IDs cannot be probed.
        if (await reservations.FindAsync(reservationId, cancellationToken) is not { } reservation
            || !(await authorization.AuthorizeAsync(http.User, reservation, ReservationOperations.PayDeposit)).Succeeded)
        {
            return TypedResults.NotFound();
        }

        var web = options.Value.WebBaseUrl ?? throw new InvalidOperationException("Payments:WebBaseUrl is not set: the success and cancel pages live in the web app's My Home.");
        var command = new StartDepositCheckoutCommand(
            reservation,
            tenant.TenantId ?? throw new InvalidOperationException("No tenant: TenantMiddleware runs before every endpoint."),
            new Uri(web, $"my-home?deposit=paid&reservation={reservationId.Value}"),
            new Uri(web, $"my-home?deposit=cancelled&reservation={reservationId.Value}"));

        return await checkout.HandleAsync(command, cancellationToken) switch
        {
            DepositCheckoutStarted started => TypedResults.Ok(new DepositSessionResponse(started.Session.Id, started.Session.Url, started.Session.ExpiresAt)),
            ReservationNotHeld notHeld => TypedResults.Conflict(Problems.Business(http, "reservation-not-held", "Reservation is not held",
                StatusCodes.Status409Conflict, $"Only a held reservation takes a deposit; this one is {notHeld.State.ToLowerInvariant()}.")),
            HoldEndsTooSoon tooSoon => TypedResults.Conflict(Problems.Business(http, "hold-ends-too-soon", "Hold ends too soon to pay online",
                StatusCodes.Status409Conflict, $"The hold ends at {tooSoon.ExpiresAt:u}, sooner than the 30 minutes a payment page needs. Ask a sales agent.")),
        };
    }
}
