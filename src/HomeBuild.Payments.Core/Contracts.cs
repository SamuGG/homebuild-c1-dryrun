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
}

public sealed record GatewayCharge(bool Succeeded, string? ProviderReference, string? FailureReason);

/// <summary>Port: where paid deposits are recorded (EF Core in HomeBuild.Payments.Infrastructure).</summary>
public interface IPaymentRecords
{
    Task<DepositPaid?> FindAsync(ReservationId reservationId, CancellationToken cancellationToken);

    Task AddAsync(DepositPaid paid, CancellationToken cancellationToken);
}
