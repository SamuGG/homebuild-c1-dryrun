using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using HomeBuild.Api.Modules.Aftercare;
using HomeBuild.Api.Scheduling;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static HomeBuild.Api.Tests.AftercareTests;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 6's time rules through the API, on a FakeTimeProvider: the acknowledgement reminder (two working days, over a
/// weekend and bank holidays), its cancellation, the visit reminder, the re-check in the handler, and the cut-off after
/// the defects period. The schedules are recorded; the message a schedule would send is handed to the handler directly.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class SnagReminderTests(PostgresFixture database)
{
    private static readonly Guid Unit = Guid.Parse(EndpointTests.RiversidePlot1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Reported on Friday 4 December 2026 at 14:00 (GMT): Saturday and Sunday do not count, so the reminder is due on
    // Tuesday 8 December at 14:00. Over Christmas, with 25 and 28 December as bank holidays, Thursday 24 December
    // 10:00 is due on Wednesday 30 December 10:00.
    [Fact]
    public async Task Reminder_due_after_two_working_days_over_weekend()
    {
        await using var t = await StartAsync(new DateTimeOffset(2026, 12, 4, 14, 0, 0, TimeSpan.Zero));
        var friday = await DraftAndReportAsync(t.Client);
        Assert.Equal(new DateTimeOffset(2026, 12, 8, 14, 0, 0, TimeSpan.Zero), t.Schedules.Active[SnagReminders.AcknowledgementName(friday)].At);

        t.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 24, 10, 0, 0, TimeSpan.Zero));
        var christmas = await DraftAndReportAsync(t.Client);
        Assert.Equal(new DateTimeOffset(2026, 12, 30, 10, 0, 0, TimeSpan.Zero), t.Schedules.Active[SnagReminders.AcknowledgementName(christmas)].At);
    }

    // Acknowledging clears the condition: the schedule is deleted, and if it fires anyway (a delete that failed, or a
    // message already in the queue) the handler finds the snag acknowledged and sends nothing.
    [Fact]
    public async Task Acknowledged_snag_sends_no_reminder()
    {
        await using var t = await StartAsync(new DateTimeOffset(2026, 12, 4, 14, 0, 0, TimeSpan.Zero));
        var id = await DraftAndReportAsync(t.Client);
        var message = t.Schedules.Active[SnagReminders.AcknowledgementName(id)].Message;

        await SendAsync(t.Client, HttpMethod.Post, $"/v2/snags/{id}/acknowledge", AftercareTests.Aftercare);
        Assert.Contains(SnagReminders.AcknowledgementName(id), t.Schedules.Cancelled);
        Assert.False(t.Schedules.Active.ContainsKey(SnagReminders.AcknowledgementName(id)));

        t.Clock.Advance(TimeSpan.FromDays(5));
        await FireAsync(t.Factory, message);
        Assert.Equal(0, await RemindersAsync(t));
    }

    // The handler decides on the state now, not the state when the schedule was made: still reported → one reminder
    // (and only one, however often it fires); a visit reminder for a date that was moved → nothing; the new date's → one.
    [Fact]
    public async Task Reminder_handler_rechecks_state()
    {
        await using var t = await StartAsync(new DateTimeOffset(2026, 12, 4, 14, 0, 0, TimeSpan.Zero));
        var id = await DraftAndReportAsync(t.Client);
        var ack = t.Schedules.Active[SnagReminders.AcknowledgementName(id)];

        await Assert.ThrowsAsync<NotYetDueException>(() => FireAsync(t.Factory, ack.Message)); // early (clock skew): retried later
        t.Clock.SetUtcNow(ack.At);
        await FireAsync(t.Factory, ack.Message);
        await FireAsync(t.Factory, ack.Message); // fired twice
        Assert.Equal(1, await RemindersAsync(t));
        var staff = (await SendAsync(t.Client, HttpMethod.Get, $"/v2/snags?unitId={Unit}", AftercareTests.Aftercare)).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);
        Assert.Contains(staff.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("internal").GetBoolean() && e.GetProperty("text").GetString()!.StartsWith("Reminder: not acknowledged", StringComparison.Ordinal));

        await SendAsync(t.Client, HttpMethod.Post, $"/v2/snags/{id}/acknowledge", AftercareTests.Aftercare);
        await SendAsync(t.Client, HttpMethod.Post, $"/v2/snags/{id}/schedule", AftercareTests.Aftercare, new { subcontractor = "Northgate Plastering", visitOn = "2026-12-15" });
        var first = t.Schedules.Active[SnagReminders.VisitName(id)];
        Assert.Equal(new DateTimeOffset(2026, 12, 14, 9, 0, 0, TimeSpan.Zero), first.At); // 09:00 the day before
        await SendAsync(t.Client, HttpMethod.Post, $"/v2/snags/{id}/fix", AftercareTests.Aftercare);
        Assert.Contains(SnagReminders.VisitName(id), t.Schedules.Cancelled);
        await SendAsync(t.Client, HttpMethod.Post, $"/v2/snags/{id}/dispute", Buyer, new { reason = "Still cracked" });
        await SendAsync(t.Client, HttpMethod.Post, $"/v2/snags/{id}/schedule", AftercareTests.Aftercare, new { subcontractor = "Northgate Plastering", visitOn = "2026-12-18" });
        var second = t.Schedules.Active[SnagReminders.VisitName(id)];

        t.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 17, 9, 0, 0, TimeSpan.Zero));
        await FireAsync(t.Factory, first.Message); // the old date's message: the visit moved
        Assert.Equal(1, await RemindersAsync(t));
        await FireAsync(t.Factory, second.Message);
        Assert.Equal(2, await RemindersAsync(t));
        var buyerView = JsonSerializer.Serialize(await SendAsync(t.Client, HttpMethod.Get, "/v2/me/snags", Buyer));
        Assert.Contains("Reminder sent: visit by Northgate Plastering on 18 December 2026", buyerView, StringComparison.Ordinal);
        Assert.DoesNotContain("not acknowledged", buyerView, StringComparison.Ordinal); // Aftercare's reminder stays internal

        await using var db = AftercareModule.CreateDbContext(t.Api.ConnectionString, TestTenants.Demo);
        var kinds = (await db.Set<OutboxMessage>().AsNoTracking().Where(m => m.Type == IntegrationEvents.SnagReminderDue).ToListAsync(Ct))
            .Select(m => JsonElement.Parse(m.Payload).GetProperty("kind").GetString());
        Assert.Equal([SnagReminderMessage.AcknowledgementOverdue, SnagReminderMessage.VisitTomorrow], kinds.Order());
    }

    // Handover on 4 December 2026: the defects period ends on 3 December 2028 (two years less a day). A snag on that last
    // day is accepted; the next day it is refused (422) and the buyer is pointed to maintenance requests.
    [Fact]
    public async Task Snag_after_defects_period_is_rejected()
    {
        await using var t = await StartAsync(new DateTimeOffset(2026, 12, 4, 10, 0, 0, TimeSpan.Zero));
        await SendAsync(t.Client, HttpMethod.Put, $"/v2/units/{Unit}/handover", AftercareTests.Aftercare, new { keysHandedOver = true, meterReadings = "Gas 1", warrantiesIssued = true });

        t.Clock.SetUtcNow(new DateTimeOffset(2028, 12, 3, 23, 30, 0, TimeSpan.Zero)); // 23:30 GMT: still 3 December in London
        using (var lastDay = await RawAsync(t.Client, HttpMethod.Post, "/v2/me/snags", Buyer, new { unitId = Unit, area = "Hall", description = "Door sticks", severity = "Minor" }))
        {
            Assert.Equal(HttpStatusCode.Created, lastDay.StatusCode);
        }

        t.Clock.SetUtcNow(new DateTimeOffset(2028, 12, 4, 0, 30, 0, TimeSpan.Zero));
        using var after = await RawAsync(t.Client, HttpMethod.Post, "/v2/me/snags", Buyer, new { unitId = Unit, area = "Hall", description = "Door sticks again", severity = "Minor" });
        var body = await after.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, after.StatusCode);
        Assert.Contains("ended on 3 December 2028", body, StringComparison.Ordinal);
        Assert.Contains("maintenance request", body, StringComparison.Ordinal);
    }

    private async Task<Started> StartAsync(DateTimeOffset now)
    {
        var schedules = new RecordingSnagSchedules();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (api, factory, client) = await AftercareTests.StartAsync(database, b => b
            .UseSetting("Aftercare:BankHolidays:0", "2026-12-25")
            .UseSetting("Aftercare:BankHolidays:1", "2026-12-28")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<ISnagSchedules>(schedules);
                services.AddSingleton<TimeProvider>(clock);
            }));
        clock.SetUtcNow(now); // after the reservation and its webhook, which run on today's clock
        return new Started(api, factory, client, schedules, clock);
    }

    private static async Task<int> RemindersAsync(Started t)
    {
        await using var db = AftercareModule.CreateDbContext(t.Api.ConnectionString, TestTenants.Demo);
        return await db.Set<OutboxMessage>().CountAsync(m => m.Type == IntegrationEvents.SnagReminderDue, Ct);
    }

    private static async Task FireAsync(WebApplicationFactory<Program> factory, ScheduledMessage message)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredKeyedService<IMessageHandler>(ScheduledWorkHandler.Name)
            .HandleAsync(new QueueMessage(Guid.NewGuid().ToString(), "receipt", JsonSerializer.Serialize(message, JsonSerializerOptions.Web), new Dictionary<string, string>(), 1), Ct);
    }

    private sealed record Started(ApiFactory Api, WebApplicationFactory<Program> Factory, HttpClient Client, RecordingSnagSchedules Schedules, FakeTimeProvider Clock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    private sealed class RecordingSnagSchedules : ISnagSchedules
    {
        public ConcurrentDictionary<string, (DateTimeOffset At, ScheduledMessage Message)> Active { get; } = new();

        public ConcurrentBag<string> Cancelled { get; } = [];

        public Task ScheduleAsync(string name, DateTimeOffset at, ScheduledMessage message, CancellationToken cancellationToken)
        {
            Active[name] = (at, message);
            return Task.CompletedTask;
        }

        public Task CancelAsync(string name, CancellationToken cancellationToken)
        {
            Cancelled.Add(name);
            Active.TryRemove(name, out _);
            return Task.CompletedTask;
        }
    }
}
