using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales;

internal sealed record ReservationResponse(
    Guid Id, Guid UnitId, Guid BuyerId, decimal Deposit, string Currency, string State, DateTimeOffset ExpiresAt)
{
    public static ReservationResponse From(Reservation r) => new(
        r.Id.Value, r.UnitId.Value, r.BuyerId.Value, r.Deposit.Amount, r.Deposit.Currency, r.State.GetType().Name, r.ExpiresAt);
}
