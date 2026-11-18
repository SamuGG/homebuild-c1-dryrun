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
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();
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
        await using var db = ContractsDbContext.Create(connection);
        await db.Database.MigrateAsync();
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

    public static WorkerHarness Create(string connection, ConsumerOptions? options = null, InMemoryDocuments? documents = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:HomeBuild"] = connection }).Build();
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning)).AddWorkers(configuration);
        documents ??= new InMemoryDocuments();
        services.Replace(ServiceDescriptor.Scoped<IContractDocuments>(_ => documents));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var queue = new InMemoryQueue();
        var consumer = new QueueConsumer(ContractWorker.Name, queue, provider.GetRequiredService<IServiceScopeFactory>(),
            options ?? new ConsumerOptions { Wait = TimeSpan.FromMilliseconds(50) }, NullLogger<QueueConsumer>.Instance);
        return new WorkerHarness(provider, queue, documents, consumer, connection);
    }

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

    public static QueueMessage DepositPaid(Guid? messageId = null, Guid? reservationId = null)
    {
        var paid = new DepositPaidMessage(reservationId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.Parse("0198a000-0000-7000-8000-00000000000a"), 5000m, "GBP", "cs_test", DateTimeOffset.UtcNow);
        return new QueueMessage(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), JsonSerializer.Serialize(paid, JsonSerializerOptions.Web),
            new Dictionary<string, string> { ["type"] = IntegrationEvents.DepositPaid, ["message_id"] = (messageId ?? Guid.NewGuid()).ToString() }, 1);
    }
}

/// <summary>An in-memory queue with SQS's shape: received messages are in flight until deleted or released.</summary>
internal sealed class InMemoryQueue : IQueueClient
{
    private readonly ConcurrentQueue<QueueMessage> _visible = new();

    public ConcurrentBag<QueueMessage> Deleted { get; } = [];

    public ConcurrentBag<QueueMessage> Released { get; } = [];

    public int Visible => _visible.Count;

    public void Send(QueueMessage message) => _visible.Enqueue(message);

    public async Task<IReadOnlyList<QueueMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
    {
        var batch = new List<QueueMessage>();
        while (batch.Count < maxMessages && _visible.TryDequeue(out var message))
        {
            batch.Add(message);
        }

        if (batch.Count == 0)
        {
            await Task.Delay(wait, cancellationToken); // long polling: wait, then return empty
        }

        return batch;
    }

    public Task DeleteAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        Deleted.Add(message);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        Released.Add(message);
        _visible.Enqueue(message);
        return Task.CompletedTask;
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
