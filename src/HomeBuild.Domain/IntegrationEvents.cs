namespace HomeBuild.Domain;

/// <summary>
/// C9 Lab 2: the events that leave a module or the process, as they travel through the outbox, SNS and SQS. Plain
/// values only (no strongly typed IDs), so any consumer (a worker, a Lambda) reads them with no HomeBuild internals. The
/// SNS <c>type</c> attribute is the name without "Message".
/// </summary>
public static class IntegrationEvents
{
    public const string DepositPaid = nameof(DepositPaid);
    public const string MilestoneCompleted = nameof(MilestoneCompleted);
    public const string ReservationExpired = nameof(ReservationExpired);
    public const string ContractIssued = nameof(ContractIssued);
    public const string ExtrasUnlocked = nameof(ExtrasUnlocked);
    public const string ExtrasPaid = nameof(ExtrasPaid);
}

public sealed record DepositPaidMessage(Guid ReservationId, Guid UnitId, Guid TenantId, decimal Amount, string Currency, string SessionId, DateTimeOffset PaidAt);

/// <param name="Version">The unit's build-progress version after this completion: consumers ignore an event older than the last they applied for the unit (C9 Lab 5).</param>
/// <param name="DevelopmentId">The unit's development (C10 Lab 1): Extras' windows are set per development, and Extras may not ask Construction. Absent in messages from before C10.</param>
public sealed record MilestoneCompletedMessage(Guid UnitId, Guid TenantId, string Milestone, DateOnly CompletedAt, int Version, Guid? DevelopmentId = null);

public sealed record ReservationExpiredMessage(Guid ReservationId, Guid UnitId, Guid BuyerId, Guid TenantId, DateTimeOffset ExpiredAt);

/// <summary>C9 Lab 3: the contract worker issued the contract pack for a paid deposit (a PDF in S3).</summary>
public sealed record ContractIssuedMessage(Guid ReservationId, Guid UnitId, Guid TenantId, string DocumentKey, DateTimeOffset IssuedAt);

/// <summary>C10 Lab 1: extras a unit's buyer can now choose, because a milestone completed.</summary>
public sealed record ExtrasUnlockedMessage(Guid UnitId, Guid TenantId, Guid DevelopmentId, string Milestone, IReadOnlyList<UnlockedExtra> Extras, DateTimeOffset UnlockedAt, int Version);

/// <param name="ClosesAt">The milestone whose completion closes the extra again.</param>
public sealed record UnlockedExtra(Guid ExtraId, string Name, decimal Price, string Currency, string ClosesAt);

/// <summary>C10 Lab 2: a basket of extras was paid; <see cref="Extras"/> holds only the lines kept (refunded ones are left out).</summary>
public sealed record ExtrasPaidMessage(Guid BasketId, Guid UnitId, Guid TenantId, Guid BuyerId, IReadOnlyList<PaidExtra> Extras, decimal Total, string Currency, DateTimeOffset PaidAt);

/// <param name="UnitPrice">The price when the buyer added it (the basket's snapshot).</param>
public sealed record PaidExtra(Guid LineId, Guid ExtraId, string Name, decimal UnitPrice, string Currency, int Quantity);
