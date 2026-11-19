using System.Globalization;
using System.Text.Json;
using Amazon.Scheduler;
using Amazon.Scheduler.Model;
using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Scheduling;
using HomeBuild.Domain;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Modules.Sales;

/// <summary>Port: one-time schedules that turn a hold's end into a ReservationExpiryDue message (C9 Lab 6).</summary>
internal interface IReservationSchedules
{
    /// <summary>Creates the reservation's expiry schedule, or moves it to <paramref name="at"/> if it exists.</summary>
    Task ScheduleExpiryAsync(ReservationId id, Guid tenantId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Deletes the reservation's expiry schedule; one that already ran (and deleted itself) is not an error.</summary>
    Task CancelExpiryAsync(ReservationId id, CancellationToken cancellationToken);
}

/// <summary>
/// Sales' published service (C9 Lab 6): schedule or cancel a reservation's expiry, best effort, after the caller's commit.
/// A failure is logged and swallowed: the expiry handler re-checks the reservation's state, so a schedule left behind
/// does nothing to a paid or cancelled reservation, and the hourly sweep expires a hold whose schedule was never made.
/// </summary>
public interface IReservationExpirySchedules
{
    Task TryScheduleAsync(ReservationId id, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task TryCancelAsync(ReservationId id, CancellationToken cancellationToken);
}

internal sealed partial class ReservationExpirySchedules(IReservationSchedules schedules, ITenantContext tenant, ILogger<ReservationExpirySchedules> logger)
    : IReservationExpirySchedules
{
    public async Task TryScheduleAsync(ReservationId id, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        try
        {
            await schedules.ScheduleExpiryAsync(id, tenant.TenantId ?? throw new InvalidOperationException("No tenant for the schedule."), expiresAt, cancellationToken);
        }
#pragma warning disable CA1031 // best effort: the hourly sweep is the catch-all for a hold with no schedule
        catch (Exception e)
#pragma warning restore CA1031
        {
            ScheduleFailed(logger, e, id.Value);
        }
    }

    public async Task TryCancelAsync(ReservationId id, CancellationToken cancellationToken)
    {
        try
        {
            await schedules.CancelExpiryAsync(id, cancellationToken);
        }
#pragma warning disable CA1031 // best effort: the expiry handler leaves a paid or cancelled reservation alone
        catch (Exception e)
#pragma warning restore CA1031
        {
            CancelFailed(logger, e, id.Value);
        }
    }

    [LoggerMessage(EventId = 1801, Level = LogLevel.Warning, Message = "Could not schedule the expiry of reservation {ReservationId}; the hourly sweep will expire it")]
    private static partial void ScheduleFailed(ILogger logger, Exception exception, Guid reservationId);

    [LoggerMessage(EventId = 1802, Level = LogLevel.Warning, Message = "Could not delete the expiry schedule of reservation {ReservationId}; when it fires the handler finds the reservation no longer held")]
    private static partial void CancelFailed(ILogger logger, Exception exception, Guid reservationId);
}

/// <summary>
/// EventBridge Scheduler: one schedule per held reservation, <c>homebuild-expiry-{id}</c> in the schedule group, firing
/// once at the hold's end (<c>at()</c>, UTC, no flexible window) and deleting itself after it runs. Its target is the
/// scheduled-work queue, sent with the scheduler role (allowed to send to that queue only).
/// </summary>
internal sealed class EventBridgeReservationSchedules(IAmazonScheduler scheduler, IOptions<SchedulingOptions> options, OpenTransactions transactions)
    : IReservationSchedules
{
    public static string NameFor(ReservationId id) => $"homebuild-expiry-{id.Value}";

    public async Task ScheduleExpiryAsync(ReservationId id, Guid tenantId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        transactions.EnsureNone("EventBridge Scheduler"); // C8 Lab 4: no external call inside a database transaction
        var o = options.Value;
        // at() has whole seconds: round up, so the schedule never fires before the hold ends.
        var due = DateTime.UnixEpoch.AddSeconds(Math.Ceiling((at.UtcDateTime - DateTime.UnixEpoch).TotalSeconds));
        var expression = "at(" + due.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + ")";
        var target = new Target
        {
            Arn = o.QueueArn,
            RoleArn = o.RoleArn,
            Input = JsonSerializer.Serialize(ScheduledMessage.ExpiryDue(id.Value, tenantId, at), JsonSerializerOptions.Web),
        };
        try
        {
            await scheduler.CreateScheduleAsync(new CreateScheduleRequest
            {
                Name = NameFor(id), GroupName = o.GroupName, ScheduleExpression = expression, ScheduleExpressionTimezone = "UTC",
                FlexibleTimeWindow = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF }, ActionAfterCompletion = ActionAfterCompletion.DELETE,
                Target = target, Description = $"Expire reservation {id.Value} at the end of its hold",
            }, cancellationToken);
        }
        catch (ConflictException)
        {
            // Scheduled before (a retried request, or a hold moved): move it.
            await scheduler.UpdateScheduleAsync(new UpdateScheduleRequest
            {
                Name = NameFor(id), GroupName = o.GroupName, ScheduleExpression = expression, ScheduleExpressionTimezone = "UTC",
                FlexibleTimeWindow = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF }, ActionAfterCompletion = ActionAfterCompletion.DELETE,
                Target = target, Description = $"Expire reservation {id.Value} at the end of its hold",
            }, cancellationToken);
        }
    }

    public async Task CancelExpiryAsync(ReservationId id, CancellationToken cancellationToken)
    {
        transactions.EnsureNone("EventBridge Scheduler");
        try
        {
            await scheduler.DeleteScheduleAsync(new DeleteScheduleRequest { Name = NameFor(id), GroupName = options.Value.GroupName }, cancellationToken);
        }
        catch (ResourceNotFoundException)
        {
            // Already ran (and deleted itself), or never created: nothing to cancel.
        }
    }
}

/// <summary>No scheduler configured (CI, local runs without AWS): holds end through the hourly sweep only.</summary>
internal sealed class NoReservationSchedules : IReservationSchedules
{
    public Task ScheduleExpiryAsync(ReservationId id, Guid tenantId, DateTimeOffset at, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CancelExpiryAsync(ReservationId id, CancellationToken cancellationToken) => Task.CompletedTask;
}
