using HomeBuild.Domain;
using HomeBuild.Payments;
using Stripe;
using Stripe.Checkout;

namespace HomeBuild.Api.Modules.Payments;

/// <summary>
/// The Stripe adapter for Payments' <see cref="IPaymentGateway"/> port (C8 Lab 1). Stripe types stop here: the core sees
/// only its own records. Tests give it a <see cref="StripeClient"/> over a stub HTTP handler; CI never has a Stripe key.
/// </summary>
internal sealed class StripePaymentGateway(IStripeClient stripe, HomeBuild.Api.Data.OpenTransactions? transactions = null) : IPaymentGateway
{
    public const string TenantMetadata = "tenant_id";
    public const string UnitMetadata = "unit_id";
    public const string ReservationMetadata = "reservation_id";

    /// <summary>C10 Lab 2: what the session pays for ("extras"); absent on C8's deposit sessions.</summary>
    public const string KindMetadata = "kind";
    public const string BasketMetadata = "basket_id";
    public const string ExtrasKind = "extras";

    public async Task<CheckoutSession> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        transactions?.EnsureNone("Stripe Checkout Session create"); // C8 Lab 4
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = StripeAmounts.Currency(request.Amount),
                        UnitAmount = StripeAmounts.ToMinorUnits(request.Amount),
                        ProductData = new SessionLineItemPriceDataProductDataOptions { Name = request.Description },
                    },
                },
            ],
            ClientReferenceId = request.ReservationId.Value.ToString(),
            Metadata = new Dictionary<string, string>
            {
                [TenantMetadata] = request.TenantId.ToString(),
                [UnitMetadata] = request.UnitId.Value.ToString(),
                [ReservationMetadata] = request.ReservationId.Value.ToString(),
            },
            SuccessUrl = request.SuccessUrl.ToString(),
            CancelUrl = request.CancelUrl.ToString(),
            ExpiresAt = request.ExpiresAt.UtcDateTime,
        };

        var session = await new SessionService(stripe).CreateAsync(options, new RequestOptions { IdempotencyKey = request.IdempotencyKey }, cancellationToken);
        return new CheckoutSession(session.Id, new Uri(session.Url), new DateTimeOffset(DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc)));
    }

    public async Task<CheckoutSession> CreateBasketCheckoutSessionAsync(BasketCheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        transactions?.EnsureNone("Stripe Checkout Session create"); // C8 Lab 4
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            LineItems = [.. request.Lines.Select(line => new SessionLineItemOptions
            {
                Quantity = line.Quantity,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = StripeAmounts.Currency(line.UnitPrice),
                    UnitAmount = StripeAmounts.ToMinorUnits(line.UnitPrice),
                    ProductData = new SessionLineItemPriceDataProductDataOptions { Name = line.Name },
                },
            })],
            ClientReferenceId = request.BasketId.ToString(),
            Metadata = new Dictionary<string, string>
            {
                [KindMetadata] = ExtrasKind,
                [TenantMetadata] = request.TenantId.ToString(),
                [UnitMetadata] = request.UnitId.Value.ToString(),
                [BasketMetadata] = request.BasketId.ToString(),
            },
            SuccessUrl = request.SuccessUrl.ToString(),
            CancelUrl = request.CancelUrl.ToString(),
            ExpiresAt = request.ExpiresAt.UtcDateTime,
        };

        var session = await new SessionService(stripe).CreateAsync(options, new RequestOptions { IdempotencyKey = request.IdempotencyKey }, cancellationToken);
        return new CheckoutSession(session.Id, new Uri(session.Url), new DateTimeOffset(DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc)));
    }

    public async Task<GatewayRefund> RefundAsync(string providerReference, Money amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(amount);
        transactions?.EnsureNone("Stripe refund"); // C8 Lab 4
        var refund = await new RefundService(stripe).CreateAsync(
            new RefundCreateOptions { PaymentIntent = providerReference, Amount = StripeAmounts.ToMinorUnits(amount) },
            new RequestOptions { IdempotencyKey = idempotencyKey },
            cancellationToken);
        return new GatewayRefund(refund.Id, refund.Status);
    }

    /// <summary>A direct charge of a payment method tokenised in the browser (C6's port; used by MarkPaid, not by deposits).</summary>
    public async Task<GatewayCharge> ChargeAsync(Money amount, string paymentMethodToken, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(amount);
        transactions?.EnsureNone("Stripe charge"); // C8 Lab 4
        try
        {
            var intent = await new PaymentIntentService(stripe).CreateAsync(
                new PaymentIntentCreateOptions
                {
                    Amount = StripeAmounts.ToMinorUnits(amount),
                    Currency = StripeAmounts.Currency(amount),
                    PaymentMethod = paymentMethodToken,
                    Confirm = true,
                    AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true, AllowRedirects = "never" },
                },
                new RequestOptions { IdempotencyKey = idempotencyKey },
                cancellationToken);
            return intent.Status == "succeeded"
                ? new GatewayCharge(true, intent.Id, null)
                : new GatewayCharge(false, null, $"The payment is {intent.Status}.");
        }
        catch (StripeException e) when (e.StripeError?.Type == "card_error")
        {
            return new GatewayCharge(false, null, e.StripeError.Message);
        }
    }
}
