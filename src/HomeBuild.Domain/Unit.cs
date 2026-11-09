namespace HomeBuild.Domain;

public sealed class Unit
{
    public Unit(UnitId id, DevelopmentId developmentId, string plotNumber, Money price)
    {
        if (id == default)
        {
            throw new ArgumentException("A unit needs an ID.", nameof(id));
        }

        if (developmentId == default)
        {
            throw new ArgumentException("A unit needs a development.", nameof(developmentId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(plotNumber);
        ArgumentNullException.ThrowIfNull(price);
        ArgumentOutOfRangeException.ThrowIfZero(price.Amount);
        Id = id;
        DevelopmentId = developmentId;
        PlotNumber = plotNumber;
        Price = price;
    }

    public UnitId Id { get; }

    public DevelopmentId DevelopmentId { get; }

    public string PlotNumber { get; }

    public Money Price { get; }

    public UnitStatus Status { get; private set; } = UnitStatus.Available;

    public ReserveResult Reserve(BuyerId buyerId, Money deposit, ReservationPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deposit);
        ArgumentNullException.ThrowIfNull(policy);
        var minimumDeposit = policy.MinimumDeposit;
        var holdPeriod = policy.HoldPeriod;
        if (deposit.Currency != Price.Currency)
        {
            throw new ArgumentException($"Deposit must be in {Price.Currency}.", nameof(deposit));
        }

        if (Status != UnitStatus.Available)
        {
            return new UnitUnavailable(Id, Status);
        }

        if (deposit.Amount < minimumDeposit.Amount || deposit.Amount > Price.Amount)
        {
            return new DepositRejected(minimumDeposit, deposit);
        }

        var reservation = new Reservation(ReservationId.New(), Id, buyerId, deposit, now, now + holdPeriod);
        Status = UnitStatus.Reserved;
        return new Reserved(reservation);
    }
}
