namespace HomeBuild.Domain;

/// <summary>A payment the buyer owes because a milestone completed.</summary>
public sealed record StagePaymentDue(Milestone Milestone, Money Amount);

/// <summary>
/// Strategy: how a development splits a unit's price into stage payments. Each development has one; callers ask it what
/// is due when a milestone completes and never know which kind it is.
/// </summary>
public interface IStagePaymentSchedule
{
    /// <summary>Every stage payment for a unit at this price, in milestone order. They always add up to the price.</summary>
    IReadOnlyList<StagePaymentDue> Plan(Money price);

    /// <summary>What becomes due when <paramref name="completed"/> completes, or null if nothing does.</summary>
    StagePaymentDue? DueOn(Milestone completed, Money price) => Plan(price).SingleOrDefault(p => p.Milestone == completed);
}

/// <summary>A fixed amount at named milestones; the rest of the price is due at practical completion.</summary>
public sealed class FixedStages : IStagePaymentSchedule
{
    private readonly IReadOnlyList<(Milestone Milestone, decimal Amount)> stages;

    public FixedStages(IEnumerable<(Milestone Milestone, decimal Amount)> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        this.stages = [.. stages.OrderBy(s => s.Milestone)];
        if (this.stages.Any(s => s.Amount <= 0 || decimal.Round(s.Amount, 2) != s.Amount))
        {
            throw new ArgumentException("Each fixed stage is a positive amount in whole pence.", nameof(stages));
        }

        if (this.stages.Select(s => s.Milestone).Distinct().Count() != this.stages.Count || this.stages.Any(s => s.Milestone == Milestone.PracticalCompletion))
        {
            throw new ArgumentException("Each milestone appears once, and practical completion takes the balance.", nameof(stages));
        }
    }

    public IReadOnlyList<StagePaymentDue> Plan(Money price)
    {
        ArgumentNullException.ThrowIfNull(price);
        var fixedTotal = stages.Sum(s => s.Amount);
        if (fixedTotal > price.Amount)
        {
            throw new InvalidOperationException($"The fixed stages ({fixedTotal}) exceed the price ({price.Amount}).");
        }

        return [.. stages.Select(s => new StagePaymentDue(s.Milestone, new Money(s.Amount, price.Currency))),
            new StagePaymentDue(Milestone.PracticalCompletion, new Money(price.Amount - fixedTotal, price.Currency))];
    }
}

/// <summary>
/// Percentages of the price at milestones, adding up to 100. Each stage is rounded down to the penny and the last stage
/// takes what rounding left over (the rounding penny), so the stages always add up to the price exactly.
/// </summary>
public sealed class PercentageOfPrice : IStagePaymentSchedule
{
    private readonly IReadOnlyList<(Milestone Milestone, decimal Percent)> stages;

    public PercentageOfPrice(IEnumerable<(Milestone Milestone, decimal Percent)> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        this.stages = [.. stages.OrderBy(s => s.Milestone)];
        if (this.stages.Count == 0 || this.stages.Any(s => s.Percent <= 0) || this.stages.Sum(s => s.Percent) != 100m)
        {
            throw new ArgumentException("Percentages are positive and add up to exactly 100.", nameof(stages));
        }

        if (this.stages.Select(s => s.Milestone).Distinct().Count() != this.stages.Count)
        {
            throw new ArgumentException("Each milestone appears once.", nameof(stages));
        }
    }

    public IReadOnlyList<StagePaymentDue> Plan(Money price)
    {
        ArgumentNullException.ThrowIfNull(price);
        var due = stages.Select(s => (s.Milestone, Amount: decimal.Floor(price.Amount * s.Percent) / 100m)).ToList();
        var remainder = price.Amount - due.Sum(d => d.Amount);
        due[^1] = (due[^1].Milestone, due[^1].Amount + remainder);
        return [.. due.Select(d => new StagePaymentDue(d.Milestone, new Money(d.Amount, price.Currency)))];
    }
}
