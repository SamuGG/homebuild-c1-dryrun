using HomeBuild.Domain;

namespace HomeBuild.Payments;

/// <summary>Takes reservation deposits: checks the command, charges once, records the payment.</summary>
public sealed class DepositPayments(IPaymentGateway gateway, IPaymentRecords records, TimeProvider time)
{
    public async Task<DepositPaymentResult> TakeAsync(TakeDepositPayment command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (DepositPaymentRules.Problem(command) is { } problem)
        {
            return new DepositPaymentFailed(command.ReservationId, problem);
        }

        if (await records.FindAsync(command.ReservationId, cancellationToken) is { } already)
        {
            return already;
        }

        var charge = await gateway.ChargeAsync(command.Deposit, command.PaymentMethodToken, DepositPaymentRules.IdempotencyKey(command), cancellationToken);
        if (!charge.Succeeded)
        {
            return new DepositPaymentFailed(command.ReservationId, charge.FailureReason ?? "The payment provider declined the charge.");
        }

        var paid = new DepositPaid(command.ReservationId, command.Deposit, charge.ProviderReference!, time.GetUtcNow());
        await records.AddAsync(paid, cancellationToken);
        return paid;
    }
}

/// <summary>The rules, internal to the module (its tests see them through InternalsVisibleTo).</summary>
internal static class DepositPaymentRules
{
    public static string? Problem(TakeDepositPayment command) =>
        command.Deposit.Amount <= 0 ? "A deposit must be more than zero."
        : string.IsNullOrWhiteSpace(command.PaymentMethodToken) ? "A payment method is required."
        : null;

    /// <summary>One charge per reservation, however often the command is retried.</summary>
    public static string IdempotencyKey(TakeDepositPayment command) => $"deposit-{command.ReservationId}";
}
