using System.Collections.Concurrent;
using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales;

internal interface IReservationStore
{
    void Add(Reservation reservation);

    Reservation? Find(ReservationId id);
}

internal sealed class InMemoryReservationStore : IReservationStore
{
    private readonly ConcurrentDictionary<ReservationId, Reservation> _reservations = new();

    public void Add(Reservation reservation) => _reservations[reservation.Id] = reservation;

    public Reservation? Find(ReservationId id) => _reservations.GetValueOrDefault(id);
}
