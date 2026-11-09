namespace HomeBuild.Domain;

public sealed class Reservation
{
    public Reservation(Guid id, Guid unitId, Guid buyerId, Money deposit, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(deposit);
        if (unitId == Guid.Empty)
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

    public Guid Id { get; }

    public Guid UnitId { get; }

    public Guid BuyerId { get; }

    public Money Deposit { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}
