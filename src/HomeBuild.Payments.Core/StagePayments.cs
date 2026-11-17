using HomeBuild.Domain;

namespace HomeBuild.Payments;

/// <summary>A stage payment the buyer of a unit owes because a milestone completed (C7 Lab 5).</summary>
public sealed record DueStagePayment(UnitId UnitId, Milestone Milestone, Money Amount, DateOnly DueFrom);

/// <summary>What the unit costs and how its development splits the price into stages.</summary>
public sealed record UnitTerms(Money Price, IStagePaymentSchedule Schedule);

/// <summary>Port: a unit's price and its development's schedule (Catalogue owns both; an adapter in the host asks it).</summary>
public interface IUnitTerms
{
    Task<UnitTerms?> TermsOfAsync(UnitId unitId, CancellationToken cancellationToken);
}

/// <summary>A due stage payment as stored: paid once <see cref="PaidAt"/> is set.</summary>
public sealed record StoredStagePayment(DueStagePayment Due, DateTimeOffset? PaidAt, string? ProviderReference);

/// <summary>Port: where stage payments are kept (EF Core in the host).</summary>
public interface IPaymentStore
{
    Task<bool> ExistsAsync(UnitId unitId, Milestone milestone, CancellationToken cancellationToken);

    Task<StoredStagePayment?> FindAsync(UnitId unitId, Milestone milestone, CancellationToken cancellationToken);

    /// <summary>Adds the payment; returns false if one already exists for that unit and milestone.</summary>
    Task<bool> AddAsync(DueStagePayment payment, CancellationToken cancellationToken);

    Task MarkPaidAsync(UnitId unitId, Milestone milestone, string providerReference, DateTimeOffset paidAt, CancellationToken cancellationToken);
}

/// <summary>Command: pay a due stage payment with a payment method the provider tokenised in the browser.</summary>
public sealed record PayStagePayment(UnitId UnitId, Milestone Milestone, string PaymentMethodToken);

public sealed record StagePaymentPaid(UnitId UnitId, Milestone Milestone, Money Amount, string ProviderReference, DateTimeOffset PaidAt);

public sealed record StagePaymentNotDue(UnitId UnitId, Milestone Milestone);

public sealed record StagePaymentDeclined(UnitId UnitId, Milestone Milestone, string Reason);

public union MarkPaidResult(StagePaymentPaid, StagePaymentNotDue, StagePaymentDeclined);

/// <summary>
/// Use case: charge a due stage payment through the gateway and mark it paid. Charging twice is impossible: a paid
/// payment returns its earlier result, and the gateway gets one idempotency key per unit and milestone.
/// </summary>
public sealed class MarkPaid(IPaymentGateway gateway, IPaymentStore store, TimeProvider time)
{
    public async Task<MarkPaidResult> HandleAsync(PayStagePayment command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await store.FindAsync(command.UnitId, command.Milestone, cancellationToken) is not { } stored)
        {
            return new StagePaymentNotDue(command.UnitId, command.Milestone);
        }

        if (stored is { PaidAt: { } paidAt, ProviderReference: { } reference })
        {
            return new StagePaymentPaid(command.UnitId, command.Milestone, stored.Due.Amount, reference, paidAt);
        }

        var charge = await gateway.ChargeAsync(stored.Due.Amount, command.PaymentMethodToken, $"stage-{command.UnitId.Value}-{command.Milestone}", cancellationToken);
        if (!charge.Succeeded)
        {
            return new StagePaymentDeclined(command.UnitId, command.Milestone, charge.FailureReason ?? "The payment provider declined the charge.");
        }

        var now = time.GetUtcNow();
        await store.MarkPaidAsync(command.UnitId, command.Milestone, charge.ProviderReference!, now, cancellationToken);
        return new StagePaymentPaid(command.UnitId, command.Milestone, stored.Due.Amount, charge.ProviderReference!, now);
    }
}

/// <summary>
/// Use case: when a milestone completes, record the stage payment the development's schedule says is due, if any.
/// Idempotent: the same event twice records one payment (checked here, and guarded by a unique key in the store).
/// </summary>
public sealed class RecordStagePaymentDue(IUnitTerms terms, IPaymentStore store) : IHandleDomainEvent<MilestoneCompleted>
{
    public async Task HandleAsync(MilestoneCompleted domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (await store.ExistsAsync(domainEvent.UnitId, domainEvent.Milestone, cancellationToken)
            || await terms.TermsOfAsync(domainEvent.UnitId, cancellationToken) is not { } unit
            || unit.Schedule.DueOn(domainEvent.Milestone, unit.Price) is not { } due
            || due.Amount.Amount == 0)
        {
            return;
        }

        await store.AddAsync(new DueStagePayment(domainEvent.UnitId, domainEvent.Milestone, due.Amount, domainEvent.CompletedAt), cancellationToken);
    }
}
