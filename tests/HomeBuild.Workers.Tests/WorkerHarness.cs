using System.Collections.Concurrent;
using System.Text.Json;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace HomeBuild.Workers.Tests;

[CollectionDefinition(Name)]
public sealed class WorkerDatabase : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Workers database";
}

/// <summary>One PostgreSQL container; each test gets a fresh, migrated database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:19").Build();
    private int _databases;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"workers_{Interlocked.Increment(ref _databases)}";
        await using (var admin = new Npgsql.NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connection = new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ToString();
        await using (var db = ContractsDbContext.Create(connection))
        {
            await db.Database.MigrateAsync();
        }

        await using (var notifications = NotificationsDbContext.Create(connection))
        {
            await notifications.Database.MigrateAsync();
        }

        return connection;
    }
}

/// <summary>The worker's services over a test database, an in-memory queue and in-memory documents.</summary>
internal sealed class WorkerHarness : IAsyncDisposable
{
    private WorkerHarness(ServiceProvider services, InMemoryQueue queue, InMemoryDocuments documents, QueueConsumer consumer, string connection)
    {
        Services = services;
        Queue = queue;
        Documents = documents;
        Consumer = consumer;
        Connection = connection;
    }

    public ServiceProvider Services { get; }

    public InMemoryQueue Queue { get; }

    public InMemoryDocuments Documents { get; }

    public QueueConsumer Consumer { get; }

    public string Connection { get; }

    public static WorkerHarness Create(string connection, ConsumerOptions? options = null, InMemoryDocuments? documents = null, InMemoryQueue? queue = null,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:HomeBuild"] = connection }).Build();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning)).AddWorkers(configuration);
        documents ??= new InMemoryDocuments();
        services.Replace(ServiceDescriptor.Scoped<IContractDocuments>(_ => documents));
        configure?.Invoke(services); // a later keyed registration wins
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        queue ??= new InMemoryQueue();
        var consumer = new QueueConsumer(ContractWorker.Name, queue, provider.GetRequiredService<IServiceScopeFactory>(),
            options ?? new ConsumerOptions { Wait = TimeSpan.FromMilliseconds(50) }, NullLogger<QueueConsumer>.Instance);
        return new WorkerHarness(provider, queue, documents, consumer, connection);
    }

    /// <summary>Another consumer on the same queue and database: a second worker, or the same worker restarted.</summary>
    public QueueConsumer NewConsumer(ConsumerOptions options, string name = ContractWorker.Name, IQueueClient? queue = null) =>
        new(name, queue ?? Queue, Services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<QueueConsumer>.Instance);

    public async Task<int> ContractsAsync()
    {
        await using var db = ContractsDbContext.Create(Connection);
        return db.Contracts.Count();
    }

    public async ValueTask DisposeAsync()
    {
        Consumer.Dispose();
        await Services.DisposeAsync();
    }

    public static QueueMessage DepositPaid(Guid? messageId = null, Guid? reservationId = null, Guid? unitId = null)
    {
        var paid = new DepositPaidMessage(reservationId ?? Guid.NewGuid(), unitId ?? Guid.NewGuid(), Guid.Parse("0198a000-0000-7000-8000-00000000000a"), 5000m, "GBP", "cs_test", DateTimeOffset.UtcNow);
        return new QueueMessage(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), JsonSerializer.Serialize(paid, JsonSerializerOptions.Web),
            new Dictionary<string, string> { ["type"] = IntegrationEvents.DepositPaid, ["message_id"] = (messageId ?? Guid.NewGuid()).ToString() }, 1);
    }
}

/// <summary>
/// An in-memory queue with SQS's semantics: a received message is hidden for the visibility timeout and comes back if
/// not deleted; each receive counts; a message received maxReceiveCount times moves to the dead-letter queue at its next
/// receive (the redrive policy); <see cref="Redrive"/> moves the dead letters back (the SQS redrive, receive count reset).
/// </summary>
internal sealed class InMemoryQueue(TimeSpan? visibilityTimeout = null, int maxReceiveCount = 5) : IQueueClient
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly TimeSpan _visibility = visibilityTimeout ?? TimeSpan.FromSeconds(30);

    public ConcurrentBag<QueueMessage> Deleted { get; } = [];

    public ConcurrentBag<QueueMessage> Released { get; } = [];

    /// <summary>Every receive, in order: (provider ID, receive count).</summary>
    public ConcurrentQueue<(string Id, int Count)> Receives { get; } = new();

    /// <summary>Every visibility change that was not a release: (provider ID, timeout).</summary>
    public ConcurrentQueue<(string Id, TimeSpan Timeout)> VisibilityChanges { get; } = new();

    public List<QueueMessage> DeadLetters { get; } = [];

    public int Visible
    {
        get
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                return _entries.Count(e => e.VisibleAt <= now);
            }
        }
    }

    public int DeadLetterCount
    {
        get
        {
            lock (_gate)
            {
                return DeadLetters.Count;
            }
        }
    }

    public void Send(QueueMessage message)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(message));
        }
    }

    /// <summary>The SQS redrive: dead letters back to this queue, receive count reset.</summary>
    public int Redrive()
    {
        lock (_gate)
        {
            var moved = DeadLetters.Count;
            _entries.AddRange(DeadLetters.Select(m => new Entry(m)));
            DeadLetters.Clear();
            return moved;
        }
    }

    public async Task<IReadOnlyList<QueueMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
    {
        var batch = new List<QueueMessage>();
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _entries.Where(e => e.VisibleAt <= now).ToList())
            {
                if (batch.Count == maxMessages)
                {
                    break;
                }

                if (entry.Count >= maxReceiveCount)
                {
                    _entries.Remove(entry); // the redrive policy: parked instead of delivered a sixth time
                    DeadLetters.Add(entry.Message);
                    continue;
                }

                entry.Count++;
                entry.Handle = Guid.NewGuid().ToString();
                entry.VisibleAt = now + _visibility;
                Receives.Enqueue((entry.Message.ProviderId, entry.Count));
                batch.Add(entry.Message with { ReceiptHandle = entry.Handle, ReceiveCount = entry.Count });
            }
        }

        if (batch.Count == 0)
        {
            await Task.Delay(wait, cancellationToken); // long polling: wait, then return empty
        }

        return batch;
    }

    public Task DeleteAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Message.ProviderId == message.ProviderId);
        }

        Deleted.Add(message);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        Released.Add(message);
        Hide(message, TimeSpan.Zero);
        return Task.CompletedTask;
    }

    public Task ChangeVisibilityAsync(QueueMessage message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        VisibilityChanges.Enqueue((message.ProviderId, timeout));
        Hide(message, timeout);
        return Task.CompletedTask;
    }

    public Task<int> DepthAsync(CancellationToken cancellationToken) => Task.FromResult(Visible);

    private void Hide(QueueMessage message, TimeSpan timeout)
    {
        lock (_gate)
        {
            // Only the latest receipt handle counts (SQS ignores, or rejects, an expired one).
            if (_entries.Find(e => e.Message.ProviderId == message.ProviderId && e.Handle == message.ReceiptHandle) is { } entry)
            {
                entry.VisibleAt = DateTime.UtcNow + timeout;
            }
        }
    }

    private sealed class Entry(QueueMessage message)
    {
        public QueueMessage Message { get; } = message;

        public int Count { get; set; }

        public string? Handle { get; set; }

        public DateTime VisibleAt { get; set; } = DateTime.MinValue;
    }
}

internal sealed class InMemoryDocuments : IContractDocuments
{
    public ConcurrentDictionary<string, byte[]> Stored { get; } = new();

    public int Puts;

    public Func<string, Task>? Before { get; set; }

    public async Task PutAsync(string key, byte[] pdf, CancellationToken cancellationToken)
    {
        if (Before is not null)
        {
            await Before(key);
        }

        Interlocked.Increment(ref Puts);
        Stored[key] = pdf;
    }
}
