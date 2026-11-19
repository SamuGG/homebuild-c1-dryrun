using System.Diagnostics;
using System.Text.Json;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HomeBuild.Workers.Tests.ContractWorkerTests;

namespace HomeBuild.Workers.Tests;

/// <summary>C9 Lab 5's acceptance tests: order per unit through versions, over a standard (unordered) queue.</summary>
[Collection(WorkerDatabase.Name)]
public sealed class OrderingTests(PostgresFixture database)
{
    private static readonly Guid Tenant = Guid.Parse("0198a000-0000-7000-8000-00000000000a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The queue delivers "frame" (version 2) before "foundations" (version 1): the older event is ignored, and a newer
    // one after it is applied. The buyer never reads "foundations" after "frame".
    [Fact]
    public async Task Older_milestone_event_after_newer_is_ignored()
    {
        var unit = Guid.NewGuid();
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync());
        using var consumer = worker.NewConsumer(new ConsumerOptions { Processors = 1, Wait = TimeSpan.FromMilliseconds(20) }, NotificationWorker.Name);
        worker.Queue.Send(Milestone(unit, "Frame", 2));
        worker.Queue.Send(Milestone(unit, "Foundations", 1));
        worker.Queue.Send(Milestone(unit, "Roof", 3));

        await consumer.StartAsync(Ct);
        await WaitAsync(() => worker.Queue.Deleted.Count == 3);
        await consumer.StopAsync(Ct);

        var seen = await MessagesAsync(worker, unit);
        TestContext.Current.TestOutputHelper?.WriteLine($"delivered Frame v2, Foundations v1, Roof v3; buyer reads [{string.Join(", ", seen)}]");
        Assert.Equal(["Frame", "Roof"], seen);
        Assert.Equal(3, worker.Queue.Deleted.Count); // the stale one is deleted, not retried
        await using var db = NotificationsDbContext.Create(worker.Connection);
        Assert.Equal(3, (await db.Set<AggregateVersion>().SingleAsync(v => v.AggregateId == unit, Ct)).Version);
    }

    // Order is per unit only: events for different units do not wait for each other. 8 units × 4 milestones, shuffled,
    // a 200 ms handler and 4 processors: four at a time, and each unit's buyer reads its milestones in version order.
    [Fact]
    public async Task Events_for_different_units_process_in_parallel()
    {
        var units = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();
        var names = new[] { "Foundations", "Frame", "Roof", "FirstFix" };
        var options = new ConsumerOptions { Processors = 4, Wait = TimeSpan.FromMilliseconds(20) };
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync(), configure: services =>
            services.AddKeyedScoped<IMessageHandler>(NotificationWorker.Name, (sp, _) =>
                new SlowHandler(new NotificationWorker(sp.GetRequiredService<NotificationsDbContext>(), TimeProvider.System), TimeSpan.FromMilliseconds(200))));
        using var consumer = worker.NewConsumer(options, NotificationWorker.Name);
        var messages = units.SelectMany(u => names.Select((n, i) => Milestone(u, n, i + 1))).ToList();
        foreach (var message in messages.OrderBy(_ => Random.Shared.Next()))
        {
            worker.Queue.Send(message);
        }

        var clock = Stopwatch.StartNew();
        await consumer.StartAsync(Ct);
        await WaitAsync(() => worker.Queue.Deleted.Count == messages.Count);
        clock.Stop();
        await consumer.StopAsync(Ct);

        var applied = 0;
        foreach (var unit in units)
        {
            var seen = await MessagesAsync(worker, unit);
            applied += seen.Count;
            var versions = seen.Select(n => Array.IndexOf(names, n) + 1).ToList();
            Assert.Equal(versions.Order(), versions); // never an older milestone after a newer one
            Assert.Equal("FirstFix", seen[^1]); // the newest always arrives in the end
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{messages.Count} events for {units.Count} units in {clock.ElapsedMilliseconds} ms (serial would be {messages.Count * 200} ms); " +
            $"max in flight {consumer.MaxInFlight}; applied {applied}, ignored as stale {messages.Count - applied}");
        Assert.Equal(options.Processors, consumer.MaxInFlight);
        Assert.InRange(clock.ElapsedMilliseconds, 0, messages.Count * 200 / 2);
    }

    internal static QueueMessage Milestone(Guid unit, string milestone, int version) =>
        new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            JsonSerializer.Serialize(new MilestoneCompletedMessage(unit, Tenant, milestone, new DateOnly(2026, 11, version), version), JsonSerializerOptions.Web),
            new Dictionary<string, string> { ["type"] = IntegrationEvents.MilestoneCompleted, ["message_id"] = Guid.NewGuid().ToString() }, 1);

    /// <summary>The milestones the buyer of a unit reads, in the order they were written.</summary>
    private static async Task<List<string>> MessagesAsync(WorkerHarness worker, Guid unit)
    {
        await using var db = NotificationsDbContext.Create(worker.Connection);
        var texts = await db.Notifications.Where(n => n.UnitId == unit).OrderBy(n => n.Sequence).Select(n => n.Text).ToListAsync(Ct);
        return [.. texts.Select(t => t[(t.IndexOf('(', StringComparison.Ordinal) + 1)..t.IndexOf(')', StringComparison.Ordinal)])];
    }

    private sealed class SlowHandler(IMessageHandler inner, TimeSpan delay) : IMessageHandler
    {
        public async Task HandleAsync(QueueMessage message, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            await inner.HandleAsync(message, cancellationToken);
        }
    }
}
