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

/// <summary>Port: where due stage payments are kept (EF Core in the host).</summary>
public interface IStagePaymentStore
{
    Task<bool> ExistsAsync(UnitId unitId, Milestone milestone, CancellationToken cancellationToken);

    /// <summary>Adds the payment; returns false if one already exists for that unit and milestone.</summary>
    Task<bool> AddAsync(DueStagePayment payment, CancellationToken cancellationToken);
}

/// <summary>
/// Use case: when a milestone completes, record the stage payment the development's schedule says is due, if any.
/// Idempotent: the same event twice records one payment (checked here, and guarded by a unique key in the store).
/// </summary>
public sealed class RecordStagePaymentDue(IUnitTerms terms, IStagePaymentStore store) : IHandleDomainEvent<MilestoneCompleted>
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
