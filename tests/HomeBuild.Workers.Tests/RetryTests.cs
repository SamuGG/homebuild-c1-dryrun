using System.Diagnostics;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static HomeBuild.Workers.Tests.ContractWorkerTests;

namespace HomeBuild.Workers.Tests;

/// <summary>C9 Lab 4's acceptance tests: retries, the dead-letter queue and the redrive, over an SQS-like in-memory queue.</summary>
[Collection(WorkerDatabase.Name)]
public sealed class RetryTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConsumerOptions Fast(int processors = 4) => new()
    {
        Processors = processors,
        Wait = TimeSpan.FromMilliseconds(20),
        VisibilityTimeout = TimeSpan.FromSeconds(30),
        RetryDelay = TimeSpan.FromMilliseconds(20),
        MaxRetryDelay = TimeSpan.FromSeconds(1),
    };

    // A payload with an unknown unit can never become a contract: five receives, then the dead-letter queue.
    [Fact]
    public async Task Poison_message_reaches_DLQ_after_max_receives()
    {
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync(), Fast());
        var poison = WorkerHarness.DepositPaid(unitId: Guid.Empty);
        worker.Queue.Send(poison);

        var clock = Stopwatch.StartNew();
        await RunUntilAsync(worker, () => worker.Queue.DeadLetterCount == 1);

        var receives = worker.Queue.Receives.Where(r => r.Id == poison.ProviderId).Select(r => r.Count).ToList();
        TestContext.Current.TestOutputHelper?.WriteLine($"parked after receives [{string.Join(", ", receives)}] in {clock.ElapsedMilliseconds} ms; released {worker.Queue.Released.Count}, backed off {worker.Queue.VisibilityChanges.Count}");
        Assert.Equal([1, 2, 3, 4, 5], receives);
        Assert.Equal(5, worker.Queue.Released.Count); // permanent: released at once each time, no back-off
        Assert.Empty(worker.Queue.VisibilityChanges);
        Assert.Empty(worker.Queue.Deleted);
        Assert.Equal(0, worker.Queue.Visible);
        Assert.Equal(0, await worker.ContractsAsync());
        Assert.Empty(worker.Documents.Stored);
    }

    // A transient failure (an S3 timeout) is retried later each time: the back-off doubles with the receive count.
    [Fact]
    public async Task Transient_failure_backs_off_by_receive_count()
    {
        var documents = new InMemoryDocuments { Before = _ => throw new IOException("S3 timeout") };
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync(), Fast(), documents);
        worker.Queue.Send(WorkerHarness.DepositPaid());

        await RunUntilAsync(worker, () => worker.Queue.DeadLetterCount == 1);

        var delays = worker.Queue.VisibilityChanges.Select(c => c.Timeout.TotalMilliseconds).ToList();
        TestContext.Current.TestOutputHelper?.WriteLine($"back-offs [{string.Join(", ", delays)}] ms");
        Assert.Equal([20, 40, 80, 160, 320], delays);
        Assert.Empty(worker.Queue.Released);
    }

    // Standard queue: a failing message does not hold the others back. The poison here fails transiently (it backs off
    // for 0.2 + 0.4 + 0.8 + 1.6 s), so the 50 good messages are all done while it is still being retried.
    [Fact]
    public async Task Other_messages_keep_flowing_while_poison_retries()
    {
        var poisonReservation = Guid.NewGuid();
        var documents = new InMemoryDocuments
        {
            Before = key => key.Contains(poisonReservation.ToString(), StringComparison.Ordinal) ? throw new InvalidOperationException("renderer bug") : Task.CompletedTask,
        };
        var options = Fast(processors: 2);
        options.RetryDelay = TimeSpan.FromMilliseconds(200);
        options.MaxRetryDelay = TimeSpan.FromSeconds(5);
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync(), options, documents);
        var poison = WorkerHarness.DepositPaid(reservationId: poisonReservation);
        worker.Queue.Send(poison); // first in line
        foreach (var _ in Enumerable.Range(0, 50))
        {
            worker.Queue.Send(WorkerHarness.DepositPaid());
        }

        await worker.Consumer.StartAsync(Ct);
        await WaitAsync(() => worker.Queue.Deleted.Count == 50);
        var poisonReceivesWhenDone = worker.Queue.Receives.Count(r => r.Id == poison.ProviderId);
        var parkedWhenDone = worker.Queue.DeadLetterCount;
        await WaitAsync(() => worker.Queue.DeadLetterCount == 1);
        await worker.Consumer.StopAsync(Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"50 good messages done after the poison's receive {poisonReceivesWhenDone}; poison parked then: {parkedWhenDone}");
        Assert.Equal(0, parkedWhenDone); // the good ones did not wait for the poison to be parked
        Assert.Equal(50, await worker.ContractsAsync());
        Assert.Single(worker.Queue.DeadLetters);
    }

    // A bug parks a good message; after the fix, the redrive brings it back and it is processed exactly once.
    [Fact]
    public async Task Redriven_message_processes_once_after_fix()
    {
        var documents = new InMemoryDocuments { Before = _ => throw new InvalidOperationException("renderer bug") };
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync(), Fast(), documents);
        var message = WorkerHarness.DepositPaid();
        worker.Queue.Send(message);
        await RunUntilAsync(worker, () => worker.Queue.DeadLetterCount == 1);
        Assert.Equal(0, worker.Documents.Puts);

        documents.Before = null; // the fix is deployed
        Assert.Equal(1, worker.Queue.Redrive());
        using var fixedWorker = worker.NewConsumer(Fast());
        await fixedWorker.StartAsync(Ct);
        await WaitAsync(() => worker.Queue.Deleted.Count == 1);
        await Task.Delay(300, Ct); // anything left would be received again by now
        await fixedWorker.StopAsync(Ct);

        Assert.Single(worker.Queue.Deleted);
        Assert.Equal(0, worker.Queue.DeadLetterCount);
        Assert.Equal(0, worker.Queue.Visible);
        Assert.Equal(1, await worker.ContractsAsync());
        Assert.Equal(1, worker.Documents.Puts);
        await using var db = ContractsDbContext.Create(worker.Connection);
        Assert.Equal(1, await db.Set<InboxMessage>().CountAsync(Ct));
        Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync(m => m.Type == IntegrationEvents.ContractIssued, Ct));
    }

    // A job longer than the visibility timeout: the heartbeat keeps the message hidden, so a second worker never gets it.
    [Fact]
    public async Task Slow_message_is_not_handed_to_a_second_worker()
    {
        var running = 0;
        var maxRunning = 0;
        var documents = new InMemoryDocuments
        {
            Before = async _ =>
            {
                var now = Interlocked.Increment(ref running);
                maxRunning = Math.Max(maxRunning, now);
                await Task.Delay(TimeSpan.FromSeconds(1.5)); // five visibility timeouts
                Interlocked.Decrement(ref running);
            },
        };
        var visibility = TimeSpan.FromMilliseconds(300);
        var options = new ConsumerOptions { Wait = TimeSpan.FromMilliseconds(20), VisibilityTimeout = visibility };
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync(), options, documents, new InMemoryQueue(visibility));
        using var second = worker.NewConsumer(options);
        worker.Queue.Send(WorkerHarness.DepositPaid());

        await second.StartAsync(Ct);
        await RunUntilAsync(worker, () => !worker.Queue.Deleted.IsEmpty);
        await Task.Delay(500, Ct);
        await second.StopAsync(Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"receives {worker.Queue.Receives.Count}, extensions {worker.Queue.VisibilityChanges.Count}, PDF writes {worker.Documents.Puts}, most at once {maxRunning}");
        Assert.Single(worker.Queue.Receives);
        Assert.Equal(1, maxRunning);
        Assert.Equal(1, worker.Documents.Puts);
        Assert.InRange(worker.Queue.VisibilityChanges.Count, 3, 10);
    }

    [Fact]
    public async Task Dead_letter_check_logs_the_depth()
    {
        var dlq = new InMemoryQueue();
        dlq.Send(WorkerHarness.DepositPaid());
        dlq.Send(WorkerHarness.DepositPaid());
        using var monitor = new DeadLetterMonitor("homebuild-contracts-dlq", dlq, TimeSpan.FromMinutes(5), NullLogger<DeadLetterMonitor>.Instance, TimeProvider.System);

        Assert.Equal(2, await monitor.CheckOnceAsync(Ct));
        Assert.Equal(2, monitor.LastDepth);
    }
}
