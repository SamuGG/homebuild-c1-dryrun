using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

/// <summary>C9 Lab 2's acceptance tests: the outbox and its relay, with an in-memory publisher (CI never calls SNS).</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class OutboxTests(PostgresFixture database)
{
    private static readonly UnitId RiversidePlot2 = new(Guid.Parse("0198a000-0000-7000-8001-000000000002"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The milestone and its MilestoneCompleted message are written by one SaveChanges, so they share a transaction.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Event_and_change_commit_together_or_not_at_all(bool commit)
    {
        await using var api = await database.CreateApiAsync();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<RequestTenantContext>().TenantId = TestTenants.Demo;
            var db = scope.ServiceProvider.GetRequiredService<ConstructionDbContext>();
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                var progress = await db.BuildProgress.SingleAsync(p => p.UnitId == RiversidePlot2, Ct);
                Assert.IsType<MilestoneDone>(progress.Complete(Milestone.Roof, "site-manager", new DateOnly(2026, 11, 18)).Value);
                await db.SaveChangesAsync(Ct);
                await (commit ? transaction.CommitAsync(Ct) : transaction.RollbackAsync(Ct));
            });
        }

        await using var check = ConstructionModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var roof = (await check.BuildProgress.AsNoTracking().SingleAsync(p => p.UnitId == RiversidePlot2, Ct))[Milestone.Roof].Status;
        var messages = await check.Set<OutboxMessage>().AsNoTracking().ToListAsync(Ct);
        if (commit)
        {
            Assert.Equal(MilestoneStatus.Completed, roof);
            var message = Assert.Single(messages);
            Assert.Equal(IntegrationEvents.MilestoneCompleted, message.Type);
            var payload = JsonSerializer.Deserialize<MilestoneCompletedMessage>(message.Payload, JsonSerializerOptions.Web)!;
            Assert.Equal((RiversidePlot2.Value, TestTenants.Demo, "Roof"), (payload.UnitId, payload.TenantId, payload.Milestone));
            Assert.Null(message.PublishedAt);
        }
        else
        {
            Assert.Equal(MilestoneStatus.InProgress, roof);
            Assert.Empty(messages); // no change, no message
        }
    }

    // Two relays (two API instances) drain one outbox at once: SKIP LOCKED gives each a different batch.
    [Fact]
    public async Task Two_relays_do_not_publish_the_same_row_concurrently()
    {
        var publisher = new RecordingPublisher { Delay = TimeSpan.FromMilliseconds(2) };
        await using var api = await database.CreateApiAsync();
        var ids = await SeedAsync(api, 300);
        await using var instanceA = WithPublisher(api, publisher);
        await using var instanceB = WithPublisher(api, publisher);
        var relayA = instanceA.Services.GetRequiredService<OutboxRelay<PaymentsDbContext>>();
        var relayB = instanceB.Services.GetRequiredService<OutboxRelay<PaymentsDbContext>>();

        var counts = await Task.WhenAll(DrainAsync(relayA), DrainAsync(relayB));

        TestContext.Current.TestOutputHelper?.WriteLine($"relay A published {counts[0]}, relay B {counts[1]}; publishes {publisher.Published.Count}, distinct {publisher.Published.Distinct().Count()}");
        Assert.Equal(ids.Order(), publisher.Published.Order()); // every row exactly once
        Assert.All(counts, c => Assert.True(c > 0, "both relays took part"));
        await using var check = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.Equal(0, await check.Set<OutboxMessage>().CountAsync(m => m.PublishedAt == null, Ct));
    }

    // A crash after publishing but before the commit rolls the marks back: the next pass publishes those rows again.
    [Fact]
    public async Task Relay_crash_after_publish_before_mark_causes_duplicate_not_loss()
    {
        var publisher = new RecordingPublisher { CrashAfter = 10 };
        await using var api = await database.CreateApiAsync();
        var ids = await SeedAsync(api, 20);
        await using var host = WithPublisher(api, publisher);
        var relay = host.Services.GetRequiredService<OutboxRelay<PaymentsDbContext>>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => relay.RelayOnceAsync(Ct));
        await using (var afterCrash = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo))
        {
            Assert.Equal(20, await afterCrash.Set<OutboxMessage>().CountAsync(m => m.PublishedAt == null, Ct)); // nothing marked
        }

        publisher.CrashAfter = null;
        Assert.Equal(20, await relay.RelayOnceAsync(Ct));

        var duplicates = publisher.Published.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        TestContext.Current.TestOutputHelper?.WriteLine($"publishes {publisher.Published.Count} for {ids.Count} rows; duplicated {duplicates.Count}");
        Assert.Equal(10, duplicates.Count); // the ten published before the crash went out twice
        Assert.Equal(ids.Order(), publisher.Published.Distinct().Order()); // and nothing was lost
        await using var check = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.All(await check.Set<OutboxMessage>().ToListAsync(Ct), m => Assert.NotNull(m.PublishedAt));
    }

    // ReservationExpired leaves Sales through its outbox, in the expiry's own transaction.
    [Fact]
    public async Task Expiry_records_ReservationExpired_in_the_outbox()
    {
        await using var api = await database.CreateApiAsync();
        using (var client = api.CreateClient())
        {
            using var reserved = await client.PostAsJsonAsync($"/v1/units/{EndpointTests.RiversidePlot1}/reservations", EndpointTests.Deposit(5000), Ct);
            Assert.Equal(System.Net.HttpStatusCode.Created, reserved.StatusCode);
        }

        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<RequestTenantContext>().TenantId = TestTenants.Demo;
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<HomeBuild.Api.Modules.Sales.ReservationExpiry>().ExpireStaleAsync(DateTimeOffset.UtcNow.AddDays(8), Ct));
        }

        await using var sales = HomeBuild.Api.Modules.Sales.SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var message = Assert.Single(await sales.Set<OutboxMessage>().AsNoTracking().ToListAsync(Ct));
        Assert.Equal(IntegrationEvents.ReservationExpired, message.Type);
        var payload = JsonSerializer.Deserialize<ReservationExpiredMessage>(message.Payload, JsonSerializerOptions.Web)!;
        Assert.Equal((Guid.Parse(EndpointTests.RiversidePlot1), TestTenants.Demo), (payload.UnitId, payload.TenantId));
    }

    // DepositPaid leaves Payments through its outbox, in the webhook's transaction; a redelivery adds nothing.
    [Fact]
    public async Task Paid_deposit_records_DepositPaid_once_in_the_outbox()
    {
        await using var api = await database.CreateApiAsync();
        await using var factory = api.WithWebHostBuilder(b => b.UseSetting("Payments:WebhookSecret", StripeTestEvents.Secret));
        using var client = factory.CreateClient();
        using var reserved = await client.PostAsJsonAsync($"/v1/units/{EndpointTests.RiversidePlot1}/reservations", EndpointTests.Deposit(5000), Ct);
        var reservation = new ReservationId(JsonElement.Parse(await reserved.Content.ReadAsStringAsync(Ct)).GetProperty("id").GetGuid());
        var payload = StripeTestEvents.Completed("evt_outbox_1", "cs_outbox_1", reservation);
        foreach (var _ in new[] { 1, 2 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Add("Stripe-Signature", StripeTestEvents.Sign(payload, StripeTestEvents.Secret, DateTimeOffset.UtcNow));
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        await using var payments = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var message = Assert.Single(await payments.Set<OutboxMessage>().AsNoTracking().ToListAsync(Ct));
        var paid = JsonSerializer.Deserialize<DepositPaidMessage>(message.Payload, JsonSerializerOptions.Web)!;
        Assert.Equal((IntegrationEvents.DepositPaid, reservation.Value, 5000m, "cs_outbox_1"), (message.Type, paid.ReservationId, paid.Amount, paid.SessionId));
    }

    private static async Task<int> DrainAsync(OutboxRelay<PaymentsDbContext> relay)
    {
        var total = 0;
        int count;
        while ((count = await relay.RelayOnceAsync(Ct)) > 0)
        {
            total += count;
        }

        return total;
    }

    private static async Task<List<Guid>> SeedAsync(ApiFactory api, int count)
    {
        await using var db = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var start = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, count).Select(i => OutboxMessage.For(IntegrationEvents.DepositPaid,
            new DepositPaidMessage(Guid.NewGuid(), Guid.NewGuid(), TestTenants.Demo, 5000m, "GBP", $"cs_{i}", start), TestTenants.Demo, start.AddMilliseconds(i))).ToList();
        db.Set<OutboxMessage>().AddRange(messages);
        await db.SaveChangesAsync(Ct);
        return [.. messages.Select(m => m.Id)];
    }

    private static WebApplicationFactory<Program> WithPublisher(ApiFactory api, RecordingPublisher publisher) =>
        api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IEventPublisher>(publisher)));

    /// <summary>The in-memory SNS: records each publish; can be slow, or "crash" after N publishes.</summary>
    private sealed class RecordingPublisher : IEventPublisher
    {
        private int _count;

        public ConcurrentQueue<Guid> PublishedQueue { get; } = new();

        public IReadOnlyList<Guid> Published => [.. PublishedQueue];

        public TimeSpan Delay { get; init; }

        public int? CrashAfter { get; set; }

        public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            PublishedQueue.Enqueue(message.Id);
            if (CrashAfter is { } n && Interlocked.Increment(ref _count) == n)
            {
                throw new InvalidOperationException("Simulated crash after publishing, before marking the rows published.");
            }
        }
    }
}
