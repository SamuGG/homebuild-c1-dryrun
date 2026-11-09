namespace HomeBuild.Domain;

public sealed record Reserved(Reservation Reservation);

public sealed record UnitUnavailable(UnitId UnitId, UnitStatus Status);

public sealed record DepositRejected(Money Minimum, Money Offered);

public union ReserveResult(Reserved, UnitUnavailable, DepositRejected);
