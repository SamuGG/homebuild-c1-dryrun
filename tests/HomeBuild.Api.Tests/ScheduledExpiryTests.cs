using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Api.Scheduling;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C9 Lab 6's acceptance tests: one-time expiry schedules. The scheduler is a fake that records what Sales asks for, the
/// clock is a FakeTimeProvider, and the message a schedule would send is handed to the scheduled-work handler directly.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class ScheduledExpiryTests(PostgresFixture database)
{
    private static readonly UnitId Plot1 = new(Guid.Parse(EndpointTests.RiversidePlot1));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Expiry_message_expires_held_reservation()
    {
        await using var api = await database.CreateApiAsync();
        var (factory, schedules, clock) = With(api);
        await using var _ = factory;
        using var client = factory.CreateClient();
        var id = await ReserveAsync(client);

        var schedule = Assert.Single(schedules.Active);
        Assert.Equal(id, schedule.Key);
        Assert.Equal(((Held)await StateAsync(api, id)).ExpiresAt, schedule.Value.At); // at the hold's end
        clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1)); // the schedule fires
        await FireAsync(factory, schedules.MessageFor(id));

        Assert.IsType<Expired>(await StateAsync(api, id));
        await using var sales = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.Equal(UnitStatus.Available, (await sales.Units.AsNoTracking().SingleAsync(u => u.Id == Plot1, Ct)).Status);
        Assert.Equal(1, await sales.Set<OutboxMessage>().CountAsync(m => m.Type == IntegrationEvents.ReservationExpired, Ct));
    }

    [Fact]
    public async Task Expiry_after_payment_does_nothing()
    {
        await using var api = await database.CreateApiAsync();
        var (factory, schedules, clock) = With(api);
        await using var _ = factory;
        using var client = factory.CreateClient();
        var id = await ReserveAsync(client);
        var message = schedules.MessageFor(id);
        await PayAsync(client, id);

        clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        await FireAsync(factory, message); // a schedule that fires anyway (late delete, or a duplicate)

        Assert.IsType<Confirmed>(await StateAsync(api, id));
        await using var sales = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.Equal(0, await sales.Set<OutboxMessage>().CountAsync(m => m.Type == IntegrationEvents.ReservationExpired, Ct));
    }

    [Fact]
    public async Task Paying_deletes_the_schedule()
    {
        await using var api = await database.CreateApiAsync();
        var (factory, schedules, _) = With(api);
        await using var __ = factory;
        using var client = factory.CreateClient();
        var id = await ReserveAsync(client);
        Assert.True(schedules.Active.ContainsKey(id));

        await PayAsync(client, id);

        Assert.False(schedules.Active.ContainsKey(id));
        Assert.Equal([id], schedules.Cancelled);
    }

    [Fact]
    public async Task Cancelling_deletes_the_schedule()
    {
        await using var api = await database.CreateApiAsync();
        var (factory, schedules, _) = With(api);
        await using var __ = factory;
        using var client = factory.CreateClient();
        var id = await ReserveAsync(client);

        using var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/v1/reservations/{id.Value}");
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(cancel, Ct)).StatusCode);

        Assert.Empty(schedules.Active);
        Assert.Equal([id], schedules.Cancelled);
    }

    // The Break, kept: the deposit is paid but the schedule cannot be deleted (Scheduler down), so it fires. The handler
    // re-checks the state and leaves the confirmed reservation alone.
    [Fact]
    public async Task Expiry_after_payment_does_nothing_when_the_schedule_could_not_be_deleted()
    {
        await using var api = await database.CreateApiAsync();
        var (factory, schedules, clock) = With(api);
        await using var _ = factory;
        schedules.FailCancel = true;
        using var client = factory.CreateClient();
        var id = await ReserveAsync(client);
        await PayAsync(client, id); // the webhook still answers 200: the delete is best effort

        Assert.True(schedules.Active.ContainsKey(id)); // still there
        clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        await FireAsync(factory, schedules.MessageFor(id));

        Assert.IsType<Confirmed>(await StateAsync(api, id));
    }

    // Early (clock skew): the reservation stays held and the handler throws a transient failure, so the consumer retries.
    [Fact]
    public async Task Expiry_message_before_the_end_is_retried_later()
    {
        await using var api = await database.CreateApiAsync();
        var (factory, schedules, _) = With(api);
        await using var __ = factory;
        using var client = factory.CreateClient();
        var id = await ReserveAsync(client);

        await Assert.ThrowsAsync<NotYetDueException>(() => FireAsync(factory, schedules.MessageFor(id)));

        Assert.IsType<Held>(await StateAsync(api, id));
    }

    // "Never": no schedule was created (Scheduler down at reserve time). The hourly clean-up's sweep expires the hold, and
    // deletes idempotency keys past 24 hours and outbox rows published more than 7 days ago.
    [Fact]
    public async Task Hourly_cleanup_expires_holds_whose_schedule_never_fired()
    {
        await using var api = await database.CreateApiAsync();
        var (factory, schedules, clock) = With(api);
        await using var _ = factory;
        schedules.FailSchedule = true;
        using var client = factory.CreateClient();
        var id = await ReserveAsync(client); // 201 although the schedule failed
        Assert.Empty(schedules.Active);
        await using (var owner = new Npgsql.NpgsqlConnection(api.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            await using var old = new Npgsql.NpgsqlCommand("""
                UPDATE idempotency.idempotency_keys SET created_at = now() - interval '25 hours';
                INSERT INTO sales.outbox_messages (id, type, payload, tenant_id, occurred_at, published_at, attempts)
                  VALUES (gen_random_uuid(), 'ReservationExpired', '{}', @tenant, now() - interval '9 days', now() - interval '8 days', 1);
                """, owner);
            old.Parameters.AddWithValue("tenant", TestTenants.Demo);
            await old.ExecuteNonQueryAsync(Ct);
        }

        clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(30)); // the next hourly run after the hold ended
        await FireAsync(factory, JsonSerializer.Serialize(new ScheduledMessage(ScheduledMessage.HourlyCleanup), JsonSerializerOptions.Web));

        Assert.IsType<Expired>(await StateAsync(api, id));
        await using var check = new Npgsql.NpgsqlConnection(api.OwnerConnectionString);
        await check.OpenAsync(Ct);
        await using var counts = new Npgsql.NpgsqlCommand("SELECT (SELECT count(*) FROM idempotency.idempotency_keys), (SELECT count(*) FROM sales.outbox_messages WHERE published_at IS NOT NULL)", check);
        await using var reader = await counts.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
    }

    private static (WebApplicationFactory<Program> Factory, RecordingSchedules Schedules, FakeTimeProvider Clock) With(ApiFactory api)
    {
        var schedules = new RecordingSchedules();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Payments:WebhookSecret", StripeTestEvents.Secret);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IReservationSchedules>(schedules);
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        return (factory, schedules, clock);
    }

    private static async Task<ReservationId> ReserveAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{Plot1.Value}/reservations") { Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer"));
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"reserve: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return new ReservationId(JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)).GetProperty("id").GetGuid());
    }

    private static async Task PayAsync(HttpClient client, ReservationId id)
    {
        var payload = StripeTestEvents.Completed("evt_" + Guid.NewGuid().ToString("N"), "cs_" + Guid.NewGuid().ToString("N"), id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", StripeTestEvents.Sign(payload, StripeTestEvents.Secret, DateTimeOffset.UtcNow));
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>What the consumer does with a message from the scheduled-work queue (each in its own scope).</summary>
    private static async Task FireAsync(WebApplicationFactory<Program> factory, string body)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredKeyedService<IMessageHandler>(ScheduledWorkHandler.Name)
            .HandleAsync(new QueueMessage(Guid.NewGuid().ToString(), "receipt", body, new Dictionary<string, string>(), 1), Ct);
    }

    private static async Task<ReservationState> StateAsync(ApiFactory api, ReservationId id)
    {
        await using var sales = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        return (await sales.Reservations.AsNoTracking().SingleAsync(r => r.Id == id, Ct)).State;
    }

    /// <summary>Records the schedules Sales asks for; can fail like an unreachable Scheduler.</summary>
    private sealed class RecordingSchedules : IReservationSchedules
    {
        public ConcurrentDictionary<ReservationId, (Guid Tenant, DateTimeOffset At)> Active { get; } = new();

        public List<ReservationId> Cancelled { get; } = [];

        public bool FailCancel { get; set; }

        public bool FailSchedule { get; set; }

        /// <summary>The body the real schedule would send (the adapter's Input).</summary>
        public string MessageFor(ReservationId id) =>
            JsonSerializer.Serialize(ScheduledMessage.ExpiryDue(id.Value, Active[id].Tenant, Active[id].At), JsonSerializerOptions.Web);

        public Task ScheduleExpiryAsync(ReservationId id, Guid tenantId, DateTimeOffset at, CancellationToken cancellationToken)
        {
            if (FailSchedule)
            {
                throw new HttpRequestException("Scheduler unreachable");
            }

            Active[id] = (tenantId, at);
            return Task.CompletedTask;
        }

        public Task CancelExpiryAsync(ReservationId id, CancellationToken cancellationToken)
        {
            if (FailCancel)
            {
                throw new HttpRequestException("Scheduler unreachable");
            }

            Active.TryRemove(id, out _);
            lock (Cancelled)
            {
                Cancelled.Add(id);
            }

            return Task.CompletedTask;
        }
    }
}
