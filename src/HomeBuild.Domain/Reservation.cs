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
    }

    public ReservationId Id { get; }

    public UnitId UnitId { get; }

    public BuyerId BuyerId { get; }

    public Money Deposit { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}
