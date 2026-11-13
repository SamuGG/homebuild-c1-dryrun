using HomeBuild.Domain;

namespace HomeBuild.Testing;

/// <summary>
/// Entry points for test data: <c>using static HomeBuild.Testing.Builders;</c> then
/// <c>AUnit().Reserved().InDevelopment(dev).Build()</c>. Every builder makes a valid object by default, and each call
/// changes one thing, so a test states only what matters to it.
/// </summary>
public static class Builders
{
    /// <summary>The currency every default amount uses (prices, deposits, the minimum deposit).</summary>
    public const string DefaultCurrency = "GBP";

    /// <summary>A fixed clock for builders and the tests that use them.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    public static UnitBuilder AUnit() => new();

    public static ReservationBuilder AReservation() => new();

    public static PolicyBuilder APolicy() => new();

    public static BuildProgressBuilder ABuildProgress() => new();

    public static DiaryEntryBuilder ADiaryEntry() => new();

    /// <summary>An amount in <see cref="DefaultCurrency"/>.</summary>
    public static Money Amount(decimal value) => new(value, DefaultCurrency);
}

public sealed class UnitBuilder
{
    private UnitId id = UnitId.New();
    private DevelopmentId developmentId = DevelopmentId.New();
    private string plotNumber = "P-01";
    private Money price = Builders.Amount(250_000m);
    private UnitSpecification? specification;
    private BuyerId? reservedBy;

    public UnitBuilder WithId(UnitId value)
    {
        id = value;
        return this;
    }

    public UnitBuilder InDevelopment(DevelopmentId value)
    {
        developmentId = value;
        return this;
    }

    public UnitBuilder WithPlot(string value)
    {
        plotNumber = value;
        return this;
    }

    public UnitBuilder PricedAt(Money value)
    {
        price = value;
        return this;
    }

    public UnitBuilder WithSpecification(UnitSpecification value)
    {
        specification = value;
        return this;
    }

    /// <summary>The unit is reserved (by <paramref name="buyer"/>, or a new buyer) with a valid default deposit.</summary>
    public UnitBuilder Reserved(BuyerId? buyer = null)
    {
        reservedBy = buyer ?? BuyerId.New();
        return this;
    }

    public Unit Build()
    {
        var unit = new Unit(id, developmentId, plotNumber, price, specification);
        if (reservedBy is { } buyer
            && unit.Reserve(buyer, new Money(5_000m, price.Currency), new PolicyBuilder().InCurrency(price.Currency).Build(), Builders.Now).Value is not HomeBuild.Domain.Reserved)
        {
            throw new InvalidOperationException("The builder's default reservation was not accepted.");
        }

        return unit;
    }
}

public sealed class ReservationBuilder
{
    private ReservationId id = ReservationId.New();
    private UnitId unitId = UnitId.New();
    private BuyerId buyerId = BuyerId.New();
    private Money deposit = Builders.Amount(5_000m);
    private DateTimeOffset createdAt = Builders.Now;
    private TimeSpan hold = TimeSpan.FromDays(7);
    private Func<Reservation, TransitionResult>? transition;

    public ReservationBuilder WithId(ReservationId value)
    {
        id = value;
        return this;
    }

    public ReservationBuilder ForUnit(UnitId value)
    {
        unitId = value;
        return this;
    }

    public ReservationBuilder By(BuyerId value)
    {
        buyerId = value;
        return this;
    }

    public ReservationBuilder WithDeposit(Money value)
    {
        deposit = value;
        return this;
    }

    public ReservationBuilder CreatedAt(DateTimeOffset value)
    {
        createdAt = value;
        return this;
    }

    public ReservationBuilder HeldFor(TimeSpan value)
    {
        hold = value;
        return this;
    }

    public ReservationBuilder Confirmed()
    {
        transition = r => r.Confirm(r.CreatedAt.AddHours(1));
        return this;
    }

    public ReservationBuilder Cancelled()
    {
        transition = r => r.Cancel(r.CreatedAt.AddHours(1));
        return this;
    }

    public ReservationBuilder Expired()
    {
        transition = r => r.Expire(r.ExpiresAt);
        return this;
    }

    public Reservation Build()
    {
        var reservation = new Reservation(id, unitId, buyerId, deposit, createdAt, createdAt + hold);
        if (transition?.Invoke(reservation).Value is InvalidTransition invalid)
        {
            throw new InvalidOperationException($"The builder's transition was refused: {invalid.Reason}");
        }

        return reservation;
    }
}

public sealed class PolicyBuilder
{
    private TimeSpan hold = TimeSpan.FromDays(7);
    private Money minimumDeposit = Builders.Amount(1_000m);

    public PolicyBuilder WithHold(TimeSpan value)
    {
        hold = value;
        return this;
    }

    public PolicyBuilder WithMinimumDeposit(Money value)
    {
        minimumDeposit = value;
        return this;
    }

    /// <summary>The same minimum deposit amount in another currency (for units priced in it).</summary>
    public PolicyBuilder InCurrency(string currency)
    {
        minimumDeposit = new Money(minimumDeposit.Amount, currency);
        return this;
    }

    public ReservationPolicy Build() => new(hold, minimumDeposit);
}

public sealed class BuildProgressBuilder
{
    /// <summary>The day the builder's builds start; milestone N is planned 21 × (N + 1) days later.</summary>
    public static readonly DateOnly StartOn = new(2026, 6, 1);

    private UnitId unitId = UnitId.New();
    private DevelopmentId developmentId = DevelopmentId.New();
    private int completed;

    public BuildProgressBuilder ForUnit(UnitId value)
    {
        unitId = value;
        return this;
    }

    public BuildProgressBuilder InDevelopment(DevelopmentId value)
    {
        developmentId = value;
        return this;
    }

    /// <summary>The first <paramref name="count"/> milestones are completed, each on its planned date.</summary>
    public BuildProgressBuilder CompletedThrough(int count)
    {
        completed = count;
        return this;
    }

    public BuildProgress Build()
    {
        var progress = BuildProgress.Start(unitId, developmentId, StartOn);
        foreach (var milestone in BuildProgress.Order.Take(completed))
        {
            if (progress.Complete(milestone, "site-manager", progress[milestone].PlannedOn).Value is not MilestoneDone)
            {
                throw new InvalidOperationException($"The builder could not complete {milestone}.");
            }
        }

        return progress;
    }
}

public sealed class DiaryEntryBuilder
{
    private UnitId unitId = UnitId.New();
    private DateOnly writtenOn = BuildProgressBuilder.StartOn;
    private string text = "Site tidy; scaffold inspected.";

    public DiaryEntryBuilder ForUnit(UnitId value)
    {
        unitId = value;
        return this;
    }

    public DiaryEntryBuilder WrittenOn(DateOnly value)
    {
        writtenOn = value;
        return this;
    }

    public DiaryEntryBuilder Saying(string value)
    {
        text = value;
        return this;
    }

    public DiaryEntry Build() => new(DiaryEntryId.New(), unitId, writtenOn, text);
}
