using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales;

/// <summary>A unit's most recent reservation, as other modules may see it.</summary>
public sealed record LatestReservation(UnitId UnitId, ReservationId ReservationId, string Status, DateTimeOffset CreatedAt);

/// <summary>One of a buyer's reservations, as other modules may see it.</summary>
public sealed record BuyerReservation(ReservationId ReservationId, UnitId UnitId, string State, DateTimeOffset ExpiresAt);

/// <summary>Sales' published read surface: other modules ask it about availability and reservations.</summary>
public interface IUnitAvailability
{
    Task<IReadOnlyDictionary<UnitId, UnitStatus>> StatusOfAsync(IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken = default);

    Task<LatestReservation?> LatestReservationAsync(UnitId unitId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<UnitId, LatestReservation>> LatestReservationsAsync(IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken = default);

    /// <summary>A buyer's reservations (the caller's tenant only), newest first.</summary>
    Task<IReadOnlyList<BuyerReservation>> ReservationsOfAsync(BuyerId buyerId, CancellationToken cancellationToken = default);
}
