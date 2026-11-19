using System.Text.Json;
using Amazon;
using Amazon.Scheduler;
using Amazon.SQS;
using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Http.Idempotency;
using HomeBuild.Api.Modules.Accounts;
using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Api.Modules.Payments.Reconcile;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Scheduling;

/// <summary>
/// C9 Lab 6 settings. With QueueArn and RoleArn set, Sales creates one-time expiry schedules; with QueueUrl set, the API
/// consumes the scheduled-work queue (expiries, the hourly clean-up, the nightly reconciliation).
/// </summary>
public sealed class SchedulingOptions
{
    public const string Section = "Scheduling";

    public string GroupName { get; set; } = "homebuild";

    /// <summary>homebuild-scheduled: the target of every schedule.</summary>
    public string? QueueArn { get; set; }

    public string? QueueUrl { get; set; }

    /// <summary>homebuild-scheduler: the role the schedules send with (sqs:SendMessage on the queue only).</summary>
    public string? RoleArn { get; set; }

    /// <summary>How long published outbox rows are kept before the hourly clean-up deletes them.</summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(7);

    public bool CreatesSchedules => QueueArn is { Length: > 0 } && RoleArn is { Length: > 0 };
}

/// <summary>
/// What a schedule sends: the body is all there is (EventBridge Scheduler's SQS target sets no message attributes), so
/// the type travels in it. Recurring schedules fill <see cref="ScheduledTime"/> from <c>&lt;aws.scheduler.scheduled-time&gt;</c>.
/// </summary>
public sealed record ScheduledMessage(string Type, Guid? ReservationId = null, Guid? TenantId = null, DateTimeOffset? ExpiresAt = null, DateTimeOffset? ScheduledTime = null)
{
    public const string ReservationExpiryDue = nameof(ReservationExpiryDue);
    public const string HourlyCleanup = nameof(HourlyCleanup);
    public const string NightlyReconciliation = nameof(NightlyReconciliation);

    public static ScheduledMessage ExpiryDue(Guid reservationId, Guid tenantId, DateTimeOffset expiresAt) => new(ReservationExpiryDue, reservationId, tenantId, expiresAt);
}

/// <summary>A message arrived before the hold's end (clock skew): retried after a back-off, like any transient failure.</summary>
public sealed class NotYetDueException(string message) : Exception(message)
{
    public NotYetDueException() : this("Not yet due.")
    {
    }

    public NotYetDueException(string message, Exception innerException) : this(message + " " + innerException?.Message)
    {
    }
}

/// <summary>
/// Handles the scheduled-work queue. Every job re-checks the current state, because a schedule can fire late, twice,
/// or after the world moved on (a paid reservation whose schedule could not be deleted).
/// </summary>
internal sealed partial class ScheduledWorkHandler(IServiceProvider services, TimeProvider time, ILogger<ScheduledWorkHandler> logger) : IMessageHandler
{
    public const string Name = "scheduled";

    public async Task HandleAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ScheduledMessage? scheduled;
        try
        {
            scheduled = JsonSerializer.Deserialize<ScheduledMessage>(message.Body, JsonSerializerOptions.Web);
        }
        catch (JsonException e)
        {
            throw new PermanentMessageException($"Message {message.ProviderId} is not a scheduled-work message.", e);
        }

        switch (scheduled)
        {
            case { Type: ScheduledMessage.ReservationExpiryDue, ReservationId: { } id, TenantId: { } tenant }:
                await ExpireAsync(new ReservationId(id), tenant, cancellationToken);
                break;
            case { Type: ScheduledMessage.HourlyCleanup }:
                await CleanUpAsync(cancellationToken);
                break;
            case { Type: ScheduledMessage.NightlyReconciliation }:
                await ReconcileAsync(scheduled.ScheduledTime ?? time.GetUtcNow(), cancellationToken);
                break;
            default:
                throw new PermanentMessageException($"Message {message.ProviderId} has an unknown or incomplete scheduled-work type ({scheduled?.Type}).");
        }
    }

    private async Task ExpireAsync(ReservationId id, Guid tenantId, CancellationToken cancellationToken)
    {
        services.GetRequiredService<RequestTenantContext>().TenantId = tenantId; // row-level security: this tenant only
        var now = time.GetUtcNow();
        var outcome = await services.GetRequiredService<ReservationExpiry>().ExpireDueAsync(id, now, cancellationToken);
        ExpiryOutcome(logger, id.Value, outcome);
        if (outcome == ExpiryResult.NotYetDue)
        {
            throw new NotYetDueException($"Reservation {id.Value} is still held at {now:O}.");
        }
    }

    /// <summary>
    /// Hourly: expired idempotency keys (C8 Lab 3: 24 hours), outbox rows published more than OutboxRetention ago, and the
    /// catch-all sweep of holds whose schedule never fired (never created, or deleted by mistake).
    /// </summary>
    private async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var retention = services.GetRequiredService<IOptions<SchedulingOptions>>().Value.OutboxRetention;
        var keys = await services.GetRequiredService<IdempotencyDbContext>().Keys
            .Where(k => k.CreatedAt < now - IdempotencyMiddleware.Lifetime).ExecuteDeleteAsync(cancellationToken);
        var published = 0;
        foreach (var outbox in new DbContext[] { services.GetRequiredService<ConstructionDbContext>(), services.GetRequiredService<PaymentsDbContext>(), services.GetRequiredService<SalesDbContext>() })
        {
            published += await outbox.Set<OutboxMessage>().Where(m => m.PublishedAt != null && m.PublishedAt < now - retention).ExecuteDeleteAsync(cancellationToken);
        }

        var connection = HomeBuildDatabase.ConnectionString(services);
        var expired = 0;
        foreach (var tenant in await TenantsAsync(connection, cancellationToken))
        {
            await using var sales = SalesModule.CreateDbContext(connection, tenant);
            expired += await new ReservationExpiry(sales).ExpireStaleAsync(now, cancellationToken);
        }

        CleanedUp(logger, keys, published, expired);
    }

    private async Task ReconcileAsync(DateTimeOffset scheduledTime, CancellationToken cancellationToken)
    {
        var report = await ActivatorUtilities.CreateInstance<ReconcileCommand>(services).RunAsync(scheduledTime.AddDays(-1), scheduledTime, cancellationToken);
        Reconciled(logger, report.From, report.To, report.StripeSessions, report.Deposits, report.Mismatches.Count);
    }

    private static async Task<IReadOnlyList<Guid>> TenantsAsync(string connection, CancellationToken cancellationToken)
    {
        await using var accounts = AccountsModule.CreateDbContext(connection);
        return await accounts.TenantMemberships.AsNoTracking().Select(m => m.TenantId).Distinct().ToListAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 1811, Level = LogLevel.Information, Message = "Expiry due for reservation {ReservationId}: {Outcome}")]
    private static partial void ExpiryOutcome(ILogger logger, Guid reservationId, ExpiryResult outcome);

    [LoggerMessage(EventId = 1812, Level = LogLevel.Information, Message = "Hourly clean-up: {Keys} idempotency keys, {Published} published outbox rows deleted; {Expired} stuck holds expired")]
    private static partial void CleanedUp(ILogger logger, int keys, int published, int expired);

    [LoggerMessage(EventId = 1813, Level = LogLevel.Information, Message = "Nightly reconciliation {From:u} to {To:u}: {Sessions} Stripe sessions, {Deposits} deposits, {Mismatches} mismatches")]
    private static partial void Reconciled(ILogger logger, DateTimeOffset from, DateTimeOffset to, int sessions, int deposits, int mismatches);
}

internal static class ScheduledWorkServices
{
    /// <summary>C9 Lab 6: reservation expiry schedules (Sales) and the consumer of the scheduled-work queue.</summary>
    public static IServiceCollection AddScheduledWork(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SchedulingOptions>().BindConfiguration(SchedulingOptions.Section);
        services.AddSingleton<IAmazonScheduler>(_ => new AmazonSchedulerClient(RegionEndpoint.EUWest2));
        // Scoped: EnsureNone reads the request's open transactions.
        services.AddScoped<IReservationSchedules>(sp => sp.GetRequiredService<IOptions<SchedulingOptions>>().Value.CreatesSchedules
            ? ActivatorUtilities.CreateInstance<EventBridgeReservationSchedules>(sp)
            : new NoReservationSchedules());
        services.AddScoped<IReservationExpirySchedules, ReservationExpirySchedules>();
        services.AddKeyedScoped<IMessageHandler, ScheduledWorkHandler>(ScheduledWorkHandler.Name);

        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration[$"{SchedulingOptions.Section}:QueueUrl"] is { Length: > 0 } queueUrl)
        {
            services.TryAddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(RegionEndpoint.EUWest2));
            var options = configuration.GetSection($"{SchedulingOptions.Section}:Consumer").Get<ConsumerOptions>() ?? new ConsumerOptions { Processors = 2, VisibilityTimeout = TimeSpan.FromMinutes(5) };
            services.AddHostedService(sp => new QueueConsumer(ScheduledWorkHandler.Name, new SqsQueueClient(sp.GetRequiredService<IAmazonSQS>(), queueUrl),
                sp.GetRequiredService<IServiceScopeFactory>(), options, sp.GetRequiredService<ILogger<QueueConsumer>>()));
        }

        return services;
    }
}
