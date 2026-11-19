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
}

public sealed record DepositPaidMessage(Guid ReservationId, Guid UnitId, Guid TenantId, decimal Amount, string Currency, string SessionId, DateTimeOffset PaidAt);

/// <param name="Version">The unit's build-progress version after this completion: consumers ignore an event older than the last they applied for the unit (C9 Lab 5).</param>
public sealed record MilestoneCompletedMessage(Guid UnitId, Guid TenantId, string Milestone, DateOnly CompletedAt, int Version);

public sealed record ReservationExpiredMessage(Guid ReservationId, Guid UnitId, Guid BuyerId, Guid TenantId, DateTimeOffset ExpiredAt);

/// <summary>C9 Lab 3: the contract worker issued the contract pack for a paid deposit (a PDF in S3).</summary>
public sealed record ContractIssuedMessage(Guid ReservationId, Guid UnitId, Guid TenantId, string DocumentKey, DateTimeOffset IssuedAt);
