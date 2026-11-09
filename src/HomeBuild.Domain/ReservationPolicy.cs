namespace HomeBuild.Domain;

public sealed record ReservationPolicy(TimeSpan HoldPeriod, Money MinimumDeposit);
