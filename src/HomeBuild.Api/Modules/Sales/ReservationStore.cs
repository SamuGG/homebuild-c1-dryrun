using System.Collections.Concurrent;
using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales;

internal interface IReservationStore
{
    void Add(Reservation reservation);

    Reservation? Find(ReservationId id);
}

/// <summary>
/// Singleton with a concurrent dictionary: correct while the data lives in process memory.
/// Wrong for EF Core in C3, where the store wraps a scoped, non-thread-safe DbContext.
/// </summary>
internal sealed class InMemoryReservationStore : IReservationStore
{
    private readonly ConcurrentDictionary<ReservationId, Reservation> _reservations = new();

    public void Add(Reservation reservation) => _reservations[reservation.Id] = reservation;

    public Reservation? Find(ReservationId id) => _reservations.GetValueOrDefault(id);
}
