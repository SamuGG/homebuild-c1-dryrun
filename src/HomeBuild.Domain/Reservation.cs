namespace HomeBuild.Domain;

public sealed class Reservation
{
    public Reservation(ReservationId id, UnitId unitId, BuyerId buyerId, Money deposit, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(deposit);
        if (unitId == default)
        {
            throw new ArgumentException("A reservation needs a unit.", nameof(unitId));
        }

        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be after creation.");
        }

        Id = id;
        UnitId = unitId;
        BuyerId = buyerId;
        Deposit = deposit;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        State = new Held(expiresAt);
    }

    public ReservationId Id { get; }

    public UnitId UnitId { get; }

    public BuyerId BuyerId { get; }

    public Money Deposit { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public ReservationState State { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public TransitionResult Expire(DateTimeOffset now) => Apply(State.Expire(now));

    public TransitionResult Confirm(DateTimeOffset depositPaidAt) => Apply(State.Confirm(depositPaidAt));

    public TransitionResult Cancel(DateTimeOffset now) => Apply(State.Cancel(now));

    private TransitionResult Apply(TransitionResult result)
    {
        if (result is Transitioned transitioned)
        {
            State = transitioned.To;
        }

        return result;
    }
}
