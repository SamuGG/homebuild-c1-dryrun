using HomeBuild.Domain;

namespace HomeBuild.Api;

public static partial class ReservationLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Unit {UnitId} reserved as {ReservationId}, expires {ExpiresAt}")]
    public static partial void UnitReserved(
        this ILogger logger, UnitId unitId, ReservationId reservationId, DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "Reservation of unit {UnitId} rejected: {Reason}")]
    public static partial void ReservationRejected(this ILogger logger, UnitId unitId, string reason);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "Reservation {ReservationId} expired at {ExpiredAt}")]
    public static partial void ReservationExpired(this ILogger logger, ReservationId reservationId, DateTimeOffset expiredAt);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "Reservation policy loaded: hold {HoldPeriod}, minimum deposit {MinimumDeposit} {Currency}")]
    public static partial void PolicyLoaded(this ILogger logger, TimeSpan holdPeriod, decimal minimumDeposit, string currency);

    public static void LogOutcome(this ILogger logger, UnitId unitId, ReserveResult result)
    {
        switch (result)
        {
            case Reserved r:
                logger.UnitReserved(unitId, r.Reservation.Id, r.Reservation.ExpiresAt);
                break;
            case UnitUnavailable:
                logger.ReservationRejected(unitId, "unit unavailable");
                break;
            case DepositRejected:
                logger.ReservationRejected(unitId, "deposit outside the allowed range");
                break;
        }
    }
}
