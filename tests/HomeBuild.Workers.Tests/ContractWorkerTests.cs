using System.Diagnostics;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Workers.Tests;

/// <summary>C9 Lab 3's acceptance tests: the contract worker over a real database and an in-memory queue.</summary>
[Collection(WorkerDatabase.Name)]
public sealed class ContractWorkerTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The relay can publish a row twice (Lab 2), and SQS can deliver a message twice: same message_id, one contract.
    [Fact]
    public async Task Duplicate_message_is_processed_once()
    {
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync());
        var messageId = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        worker.Queue.Send(WorkerHarness.DepositPaid(messageId, reservation));
        worker.Queue.Send(WorkerHarness.DepositPaid(messageId, reservation)); // a different SQS message, the same event

        await RunUntilAsync(worker, () => worker.Queue.Deleted.Count == 2);

        Assert.Equal(1, await worker.ContractsAsync());
        // Two processors can take the two copies at the same moment: both pass the inbox pre-check and write the PDF
        // (the same key, so the second overwrites the first); the inbox insert lets only one record the contract.
        Assert.InRange(worker.Documents.Puts, 1, 2);
        Assert.Single(worker.Documents.Stored);
        await using var db = ContractsDbContext.Create(worker.Connection);
        Assert.Equal(1, await db.Set<InboxMessage>().CountAsync(Ct));
        Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync(m => m.Type == IntegrationEvents.ContractIssued, Ct));
    }

    [Fact]
    public async Task Message_not_deleted_when_processing_fails()
    {
        var documents = new InMemoryDocuments { Before = _ => throw new IOException("S3 timeout") };
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync(), documents: documents);
        worker.Queue.Send(WorkerHarness.DepositPaid());

        await worker.Consumer.StartAsync(Ct);
        await WaitAsync(() => worker.Queue.Visible == 0); // taken
        await Task.Delay(300, Ct);
        await worker.Consumer.StopAsync(Ct);

        Assert.Empty(worker.Queue.Deleted); // it stays on the queue: visible again after the visibility timeout
        Assert.Equal(0, await worker.ContractsAsync());
        await using var db = ContractsDbContext.Create(worker.Connection);
        Assert.Equal(0, await db.Set<InboxMessage>().CountAsync(Ct)); // nothing recorded, so the retry does the work
    }

    // Stopping while a message is being processed: that one finishes and is deleted; the ones not started go back.
    [Fact]
    public async Task Shutdown_finishes_in_flight_work()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var documents = new InMemoryDocuments
        {
            Before = async _ =>
            {
                started.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(1)); // a slow S3 write
            },
        };
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync(),
            new ConsumerOptions { Processors = 1, ChannelCapacity = 5, Wait = TimeSpan.FromMilliseconds(50) }, documents);
        foreach (var _ in Enumerable.Range(0, 5))
        {
            worker.Queue.Send(WorkerHarness.DepositPaid());
        }

        await worker.Consumer.StartAsync(Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var clock = Stopwatch.StartNew();
        await worker.Consumer.StopAsync(Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"stopped in {clock.ElapsedMilliseconds} ms; deleted {worker.Queue.Deleted.Count}, released {worker.Queue.Released.Count}, contracts {await worker.ContractsAsync()}");
        Assert.Single(worker.Queue.Deleted); // the in-flight one finished
        Assert.Equal(1, await worker.ContractsAsync());
        Assert.Equal(4, worker.Queue.Released.Count); // the rest are visible again at once
        Assert.Equal(4, worker.Queue.Visible);
    }

    // Shutdown during a long poll: the poll is allowed to finish, and what it returns goes straight back to the queue.
    // (Cancelling it would only abandon it on this side; SQS would still deliver to it and hide those messages.)
    [Fact]
    public async Task Shutdown_releases_what_a_receive_in_progress_returns()
    {
        var queue = new PollingQueue();
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync());
        using var consumer = new QueueConsumer(ContractWorker.Name, queue, worker.Services.GetRequiredService<IServiceScopeFactory>(),
            new ConsumerOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<QueueConsumer>.Instance);

        await consumer.StartAsync(Ct);
        await queue.Polling.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var stopping = consumer.StopAsync(Ct);
        queue.Arrive([WorkerHarness.DepositPaid(), WorkerHarness.DepositPaid(), WorkerHarness.DepositPaid()]); // SQS answers the poll
        await stopping;

        Assert.Equal(3, queue.Released.Count);
        Assert.Empty(queue.Deleted);
        Assert.Equal(0, await worker.ContractsAsync());
    }

    // C11 Lab 5: a pause that comes while a long poll is open holds what that poll returns. Before, the paused consumer
    // still handled that one batch (C10's change-order journey saw "Ordered on site" while the worker was paused).
    [Fact]
    public async Task Pause_during_a_long_poll_holds_the_messages_it_returns()
    {
        var queue = new PollingQueue();
        var pauses = new ConsumerPauses();
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync());
        using var consumer = new QueueConsumer(ContractWorker.Name, queue, worker.Services.GetRequiredService<IServiceScopeFactory>(),
            new ConsumerOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<QueueConsumer>.Instance, pauses);

        await consumer.StartAsync(Ct);
        await queue.Polling.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); // the first poll is open
        pauses.Pause(ContractWorker.Name);
        queue.Arrive([WorkerHarness.DepositPaid(), WorkerHarness.DepositPaid(), WorkerHarness.DepositPaid()]); // SQS answers it
        for (var i = 0; i < 50 && queue.Released.Count < 3; i++)
        {
            await Task.Delay(100, Ct);
        }

        await consumer.StopAsync(Ct);
        Assert.Equal(3, queue.Released.Count);
        Assert.Empty(queue.Deleted);
        Assert.Equal(0, await worker.ContractsAsync());
    }

    // C11 Lab 6: a host that is not ready (its database or queue unreachable) receives nothing; once ready, it receives.
    [Fact]
    public async Task Worker_stops_receiving_when_not_ready()
    {
        var queue = new CountingQueue();
        var readiness = new ConsumerReadiness(); // not ready until the first readiness report says so
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync());
        using var consumer = new QueueConsumer(ContractWorker.Name, queue, worker.Services.GetRequiredService<IServiceScopeFactory>(),
            new ConsumerOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<QueueConsumer>.Instance, readiness: readiness);

        await consumer.StartAsync(Ct);
        await Task.Delay(1_000, Ct);
        var whileNotReady = queue.Receives;
        readiness.Set(true);
        for (var i = 0; i < 50 && queue.Receives == 0; i++)
        {
            await Task.Delay(50, Ct);
        }

        var onceReady = queue.Receives;
        readiness.Set(false);
        await Task.Delay(300, Ct); // a receive in progress finishes; then no more
        var afterNotReadyAgain = queue.Receives;
        await Task.Delay(1_000, Ct);
        await consumer.StopAsync(Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"receives: not ready {whileNotReady}, ready {onceReady}, not ready again +{queue.Receives - afterNotReadyAgain}");
        Assert.Equal(0, whileNotReady);
        Assert.True(onceReady > 0, "no receive once ready");
        Assert.Equal(afterNotReadyAgain, queue.Receives);
    }

    /// <summary>A queue that counts receives and answers each with nothing after a short wait.</summary>
    private sealed class CountingQueue : IQueueClient
    {
        private int _receives;

        public int Receives => Volatile.Read(ref _receives);

        public async Task<IReadOnlyList<QueueMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _receives);
            await Task.Delay(50, CancellationToken.None);
            return [];
        }

        public Task DeleteAsync(QueueMessage message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseAsync(QueueMessage message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ChangeVisibilityAsync(QueueMessage message, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> DepthAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }

    /// <summary>A queue whose receive is a long poll that answers only when messages arrive, whatever the token says.</summary>
    private sealed class PollingQueue : IQueueClient
    {
        private readonly TaskCompletionSource<IReadOnlyList<QueueMessage>> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _polls;

        public TaskCompletionSource Polling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<QueueMessage> Released { get; } = [];

        public List<QueueMessage> Deleted { get; } = [];

        public void Arrive(IReadOnlyList<QueueMessage> messages) => _answer.TrySetResult(messages);

        public Task<IReadOnlyList<QueueMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _polls) > 1)
            {
                return Task.FromResult<IReadOnlyList<QueueMessage>>([]);
            }

            Polling.TrySetResult();
            return _answer.Task;
        }

        public Task DeleteAsync(QueueMessage message, CancellationToken cancellationToken)
        {
            Deleted.Add(message);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(QueueMessage message, CancellationToken cancellationToken)
        {
            Released.Add(message);
            return Task.CompletedTask;
        }

        public Task ChangeVisibilityAsync(QueueMessage message, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> DepthAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }

    // A burst: the channel bounds what is held in memory and the processors bound the database connections.
    [Fact]
    public async Task Burst_of_1000_messages_respects_channel_bound()
    {
        var options = new ConsumerOptions { Processors = 4, ChannelCapacity = 20, Wait = TimeSpan.FromMilliseconds(50) };
        await using var worker = WorkerHarness.Create(await database.CreateDatabaseAsync(), options);
        foreach (var _ in Enumerable.Range(0, 1000))
        {
            worker.Queue.Send(WorkerHarness.DepositPaid());
        }

        var databaseName = new Npgsql.NpgsqlConnectionStringBuilder(worker.Connection).Database!;
        var maxConnections = 0;
        long peakMemory = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            await using var conn = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(worker.Connection) { Database = "postgres", Pooling = false }.ToString());
            await conn.OpenAsync(sampling.Token);
            while (!sampling.IsCancellationRequested)
            {
                await using var q = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM pg_stat_activity WHERE datname = '{databaseName}' AND state <> 'idle'", conn);
                maxConnections = Math.Max(maxConnections, (int)(long)(await q.ExecuteScalarAsync(sampling.Token))!);
                peakMemory = Math.Max(peakMemory, GC.GetTotalMemory(false));
                await Task.Delay(10, sampling.Token);
            }
        }, Ct);
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        var clock = Stopwatch.StartNew();

        await RunUntilAsync(worker, () => worker.Queue.Deleted.Count == 1000, TimeSpan.FromMinutes(2));

        clock.Stop();
        await sampling.CancelAsync();
        try { await sampler; } catch (OperationCanceledException) { }
        GC.Collect();
        var after = GC.GetTotalMemory(true);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"1000 messages in {clock.ElapsedMilliseconds} ms ({1000 * 1000 / Math.Max(1, clock.ElapsedMilliseconds)} msg/s); max in flight {worker.Consumer.MaxInFlight} (processors {options.Processors}); " +
            $"max in channel {worker.Consumer.MaxQueued} (capacity {options.ChannelCapacity}); max active DB connections {maxConnections}; managed memory before {before / 1024} KB, peak {peakMemory / 1024} KB, after {after / 1024} KB");
        Assert.Equal(1000, await worker.ContractsAsync());
        Assert.InRange(worker.Consumer.MaxInFlight, 1, options.Processors);
        Assert.InRange(worker.Consumer.MaxQueued, 0, options.ChannelCapacity);
        Assert.InRange(maxConnections, 0, options.Processors);
        Assert.InRange(after - before, long.MinValue, 20 * 1024 * 1024); // flat: no growth with the backlog
    }

    internal static async Task RunUntilAsync(WorkerHarness worker, Func<bool> done, TimeSpan? timeout = null)
    {
        await worker.Consumer.StartAsync(Ct);
        await WaitAsync(done, timeout);
        await worker.Consumer.StopAsync(Ct);
    }

    internal static async Task WaitAsync(Func<bool> done, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting");
            await Task.Delay(20, Ct);
        }
    }
}
