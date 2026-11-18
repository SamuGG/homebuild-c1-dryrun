using HomeBuild.Domain;

namespace HomeBuild.Payments;

// The module's public surface: one command, its results (events), and the two ports adapters implement.

/// <summary>Take the deposit for a reservation with a payment method the provider tokenised in the browser.</summary>
public sealed record TakeDepositPayment(ReservationId ReservationId, Money Deposit, string PaymentMethodToken);

/// <summary>What taking a deposit came to.</summary>
public abstract record DepositPaymentResult(ReservationId ReservationId);

/// <summary>Event: the deposit was paid (or had already been paid: taking it again is a no-op).</summary>
public sealed record DepositPaid(ReservationId ReservationId, Money Amount, string ProviderReference, DateTimeOffset PaidAt)
    : DepositPaymentResult(ReservationId);

/// <summary>Event: the deposit was not paid; nothing was charged.</summary>
public sealed record DepositPaymentFailed(ReservationId ReservationId, string Reason) : DepositPaymentResult(ReservationId);

/// <summary>Port: a card payment provider (Stripe in C8).</summary>
public interface IPaymentGateway
{
    /// <summary>Charges once per <paramref name="idempotencyKey"/>: a retry with the same key never charges twice.</summary>
    Task<GatewayCharge> ChargeAsync(Money amount, string paymentMethodToken, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// C8 Lab 1: opens a payment page hosted by the provider (Stripe Checkout) for one amount. Nothing is charged here:
    /// the buyer pays on the provider's page, and HomeBuild learns the outcome later from a signed webhook (Lab 2).
    /// </summary>
    Task<CheckoutSession> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// C8 Lab 5: refunds a payment in full, once per <paramref name="idempotencyKey"/> (a retry never refunds twice).
    /// Never call it inside a database transaction: record the intent, commit, refund, record the result.
    /// </summary>
    Task<GatewayRefund> RefundAsync(string providerReference, Money amount, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed record GatewayCharge(bool Succeeded, string? ProviderReference, string? FailureReason);

/// <summary>A refund the provider accepted (C8 Lab 5).</summary>
public sealed record GatewayRefund(string Id, string Status);

/// <summary>What the provider's hosted page asks the buyer to pay, and for which reservation.</summary>
/// <param name="IdempotencyKey">Passed to the provider (C8 Lab 3): a retried create returns the first session.</param>
public sealed record CheckoutRequest(
    ReservationId ReservationId, Guid TenantId, UnitId UnitId, Money Amount, string Description, Uri SuccessUrl, Uri CancelUrl, DateTimeOffset ExpiresAt,
    string IdempotencyKey);

/// <summary>A hosted payment page: send the buyer to <see cref="Url"/>; it stops taking payment at <see cref="ExpiresAt"/>.</summary>
public sealed record CheckoutSession(string Id, Uri Url, DateTimeOffset ExpiresAt);

/// <summary>Port: where paid deposits are recorded (EF Core in HomeBuild.Payments.Infrastructure).</summary>
public interface IPaymentRecords
{
    Task<DepositPaid?> FindAsync(ReservationId reservationId, CancellationToken cancellationToken);

    Task AddAsync(DepositPaid paid, CancellationToken cancellationToken);
}
