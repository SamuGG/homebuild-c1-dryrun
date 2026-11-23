using System.Globalization;
using System.Text.Json;
using Amazon.Scheduler;
using Amazon.Scheduler.Model;
using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Scheduling;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Modules.Aftercare;

/// <summary>Port: one-time schedules that turn a snag's deadline into a scheduled-work message (C9 Lab 6's pattern).</summary>
internal interface ISnagSchedules
{
    /// <summary>Creates the schedule, or moves it if it exists.</summary>
    Task ScheduleAsync(string name, DateTimeOffset at, ScheduledMessage message, CancellationToken cancellationToken);

    /// <summary>Deletes the schedule; one that already ran (and deleted itself) is not an error.</summary>
    Task CancelAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// C10 Lab 6: which reminder schedules a snag needs after a change. Reported: Aftercare is reminded when it is still not
/// acknowledged after two working days. Scheduled: the buyer is reminded at 09:00 the day before the visit. Leaving the
/// state cancels the schedule. Best effort, after the commit: the handler re-checks the snag, so a schedule that could
/// not be deleted does nothing.
/// </summary>
internal sealed partial class SnagReminders(ISnagSchedules schedules, WorkingCalendar calendar, IOptions<AftercareOptions> options, TimeProvider time, ILogger<SnagReminders> logger)
{
    public static string AcknowledgementName(Guid snagId) => $"homebuild-snag-ack-{snagId}";

    public static string VisitName(Guid snagId) => $"homebuild-snag-visit-{snagId}";

    /// <summary>When a snag reported at <paramref name="reportedAt"/> is overdue for acknowledgement.</summary>
    public DateTimeOffset AcknowledgementDue(DateTimeOffset reportedAt) => calendar.AddWorkingDays(reportedAt, options.Value.AcknowledgeWithinWorkingDays);

    /// <summary>When the buyer is reminded of a visit: the configured local time the day before (or now, if that has passed).</summary>
    public DateTimeOffset VisitReminderAt(DateOnly visitOn)
    {
        var at = calendar.At(visitOn.AddDays(-1), options.Value.VisitReminderAt);
        var now = time.GetUtcNow();
        return at > now ? at : now.AddMinutes(1);
    }

    public async Task AfterTransitionAsync(Snag snag, SnagState before, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snag);
        ArgumentNullException.ThrowIfNull(before);
        await TryAsync(snag.Id, async () =>
        {
            if (snag.State is SnagState.Reported && before is not SnagState.Reported && snag.ReportedAt is { } reportedAt)
            {
                var due = AcknowledgementDue(reportedAt);
                await schedules.ScheduleAsync(AcknowledgementName(snag.Id), due, ScheduledMessage.SnagDue(ScheduledMessage.SnagAcknowledgementDue, snag.Id, tenantId, due), cancellationToken);
            }
            else if (before is SnagState.Reported && snag.State is not SnagState.Reported)
            {
                await schedules.CancelAsync(AcknowledgementName(snag.Id), cancellationToken); // acknowledged: the condition cleared
            }

            if (snag.State is SnagState.Scheduled visit)
            {
                var at = VisitReminderAt(visit.VisitOn);
                await schedules.ScheduleAsync(VisitName(snag.Id), at, ScheduledMessage.SnagDue(ScheduledMessage.SnagVisitDue, snag.Id, tenantId, at, visit.VisitOn), cancellationToken);
            }
            else if (before is SnagState.Scheduled)
            {
                await schedules.CancelAsync(VisitName(snag.Id), cancellationToken);
            }
        });
    }

    private async Task TryAsync(Guid snagId, Func<Task> work)
    {
        try
        {
            await work();
        }
#pragma warning disable CA1031 // best effort: the handler re-checks state, so a stale or missing schedule is harmless
        catch (Exception e)
#pragma warning restore CA1031
        {
            ScheduleFailed(logger, e, snagId);
        }
    }

    [LoggerMessage(EventId = 1901, Level = LogLevel.Warning, Message = "Could not update the reminder schedules of snag {SnagId}")]
    private static partial void ScheduleFailed(ILogger logger, Exception exception, Guid snagId);
}

/// <summary>
/// The scheduled-work handler's snag jobs. Each re-checks the snag now: still reported (not acknowledged), or still
/// scheduled for that visit date. Only then does it record the reminder in the timeline (once) and write
/// SnagReminderDue to Aftercare's outbox, for the notifications Lambda to email.
/// </summary>
internal sealed partial class SnagReminderJob(AftercareDbContext db, TimeProvider time, IOptions<AftercareOptions> options, ILogger<SnagReminderJob> logger)
{
    public async Task<bool> RunAsync(ScheduledMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = time.GetUtcNow();
        if (message.DueAt is { } due && now < due)
        {
            throw new NotYetDueException($"Snag {message.SnagId} reminder is due at {due:O}, not {now:O}."); // clock skew: retried later
        }

        var snag = await db.Snags.FirstOrDefaultAsync(s => s.Id == message.SnagId, cancellationToken);
        var (kind, text, toAftercare) = (message.Type, snag?.State) switch
        {
            (ScheduledMessage.SnagAcknowledgementDue, SnagState.Reported) =>
                (SnagReminderMessage.AcknowledgementOverdue, $"Reminder: not acknowledged within {options.Value.AcknowledgeWithinWorkingDays} working days of being reported", true),
            (ScheduledMessage.SnagVisitDue, SnagState.Scheduled s) when s.VisitOn == message.VisitOn =>
                (SnagReminderMessage.VisitTomorrow, string.Create(CultureInfo.GetCultureInfo("en-GB"), $"Reminder sent: visit by {s.Subcontractor} on {s.VisitOn:d MMMM yyyy}"), false),
            _ => (null, null, false),
        };
        if (snag is null || kind is null || !snag.RecordReminder(text!, toAftercare, now))
        {
            Cleared(logger, message.SnagId ?? Guid.Empty, message.Type, snag?.State.Name ?? "missing");
            return false; // acknowledged, rescheduled, closed, or already reminded: nothing to send
        }

        var visit = snag.State as SnagState.Scheduled;
        db.Set<OutboxMessage>().Add(OutboxMessage.For(IntegrationEvents.SnagReminderDue,
            new SnagReminderMessage(snag.Id, snag.UnitId.Value, db.CurrentTenantId, kind, snag.Area, snag.Description, message.DueAt ?? now, visit?.Subcontractor, visit?.VisitOn),
            db.CurrentTenantId, now));
        await db.SaveChangesAsync(cancellationToken);
        Reminded(logger, snag.Id, kind);
        return true;
    }

    [LoggerMessage(EventId = 1902, Level = LogLevel.Information, Message = "Snag {SnagId}: {Type} no longer applies (state {State}); no reminder")]
    private static partial void Cleared(ILogger logger, Guid snagId, string type, string state);

    [LoggerMessage(EventId = 1903, Level = LogLevel.Information, Message = "Snag {SnagId}: {Kind} reminder written to the outbox")]
    private static partial void Reminded(ILogger logger, Guid snagId, string kind);
}

/// <summary>EventBridge Scheduler: the scheduled-work group, queue and role of C9 Lab 6; <c>at()</c> in UTC, deleted after it runs.</summary>
internal sealed class EventBridgeSnagSchedules(IAmazonScheduler scheduler, IOptions<SchedulingOptions> options, OpenTransactions transactions) : ISnagSchedules
{
    public async Task ScheduleAsync(string name, DateTimeOffset at, ScheduledMessage message, CancellationToken cancellationToken)
    {
        transactions.EnsureNone("EventBridge Scheduler");
        var o = options.Value;
        var due = DateTime.UnixEpoch.AddSeconds(Math.Ceiling((at.UtcDateTime - DateTime.UnixEpoch).TotalSeconds)); // never early
        var expression = "at(" + due.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + ")";
        var target = new Target { Arn = o.QueueArn, RoleArn = o.RoleArn, Input = JsonSerializer.Serialize(message, JsonSerializerOptions.Web) };
        var window = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF };
        try
        {
            await scheduler.CreateScheduleAsync(new CreateScheduleRequest
            {
                Name = name, GroupName = o.GroupName, ScheduleExpression = expression, ScheduleExpressionTimezone = "UTC", FlexibleTimeWindow = window,
                ActionAfterCompletion = ActionAfterCompletion.DELETE, Target = target, Description = $"{message.Type} for snag {message.SnagId}",
            }, cancellationToken);
        }
        catch (ConflictException)
        {
            await scheduler.UpdateScheduleAsync(new UpdateScheduleRequest
            {
                Name = name, GroupName = o.GroupName, ScheduleExpression = expression, ScheduleExpressionTimezone = "UTC", FlexibleTimeWindow = window,
                ActionAfterCompletion = ActionAfterCompletion.DELETE, Target = target, Description = $"{message.Type} for snag {message.SnagId}",
            }, cancellationToken);
        }
    }

    public async Task CancelAsync(string name, CancellationToken cancellationToken)
    {
        transactions.EnsureNone("EventBridge Scheduler");
        try
        {
            await scheduler.DeleteScheduleAsync(new DeleteScheduleRequest { Name = name, GroupName = options.Value.GroupName }, cancellationToken);
        }
        catch (ResourceNotFoundException)
        {
            // Already ran, or never created.
        }
    }
}

/// <summary>No scheduler configured (CI, local runs without AWS): no reminders.</summary>
internal sealed class NoSnagSchedules : ISnagSchedules
{
    public Task ScheduleAsync(string name, DateTimeOffset at, ScheduledMessage message, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CancelAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
}
