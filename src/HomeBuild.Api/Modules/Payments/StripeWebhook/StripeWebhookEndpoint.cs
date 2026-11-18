using System.Text;
using HomeBuild.Api.Configuration;
using HomeBuild.Api.Http;
using Microsoft.Extensions.Options;
using Stripe;

namespace HomeBuild.Api.Modules.Payments.StripeWebhook;

/// <summary>
/// C8 Lab 2: POST /webhooks/stripe. Anonymous (Stripe has no HomeBuild token), so the Stripe-Signature header is the
/// only proof of origin: it is checked against the raw body, byte for byte, before anything is parsed or stored.
/// </summary>
internal static partial class StripeWebhookEndpoint
{
    public const string Path = "/webhooks/stripe";

    public static RouteHandlerBuilder MapStripeWebhook(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(Path, Handle)
            .AllowAnonymous()
            .WithMetadata(new TenantNotRequiredAttribute()) // the tenant comes from the signed event, not the request
            .ExcludeFromDescription(); // for Stripe, not for API clients

    private static async Task<IResult> Handle(
        HttpRequest request, IOptions<PaymentsOptions> options, StripeDepositWebhook webhook, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(typeof(StripeWebhookEndpoint).FullName!);
        var secret = options.Value.WebhookSecret is { Length: > 0 } s ? s : throw new InvalidOperationException("Payments:WebhookSecret is not set.");

        // The raw body, never a model-bound object: re-serialised JSON is different bytes and fails the signature.
        string payload;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        {
            payload = await reader.ReadToEndAsync(cancellationToken);
        }

        // Stripe.net 53.1.0 throws NullReferenceException (not StripeException) for a missing header: check it first.
        if (request.Headers["Stripe-Signature"].ToString() is not { Length: > 0 } signature)
        {
            InvalidSignature(logger, request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", "No Stripe-Signature header.");
            return TypedResults.Problem(Problems.Business(request.HttpContext, "invalid-signature", "Invalid Stripe signature", StatusCodes.Status400BadRequest));
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signature, secret);
        }
        catch (StripeException e)
        {
            InvalidSignature(logger, request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", e.Message);
            return TypedResults.Problem(Problems.Business(request.HttpContext, "invalid-signature", "Invalid Stripe signature", StatusCodes.Status400BadRequest));
        }

        var (result, reservation) = await webhook.HandleAsync(stripeEvent, cancellationToken);
        Handled(logger, stripeEvent.Id, stripeEvent.Type, result, reservation?.Value);
        return TypedResults.Ok();
    }

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "Stripe webhook rejected from {RemoteIp}: {Reason}")]
    private static partial void InvalidSignature(ILogger logger, string remoteIp, string reason);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Information, Message = "Stripe event {EventId} ({EventType}): {Result}, reservation {ReservationId}")]
    private static partial void Handled(ILogger logger, string eventId, string eventType, WebhookResult result, Guid? reservationId);
}
