using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeBuild.Messaging;

/// <summary>Port: where outbox messages go (SNS in the app, an in-memory fake in tests and CI).</summary>
public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public sealed class MessagingOptions
{
    public const string Section = "Messaging";

    /// <summary>The <c>homebuild-events</c> topic's ARN (from user-secrets or the environment: it holds the account ID).</summary>
    public string? TopicArn { get; set; }

    /// <summary>Runs the relays in this host. Off when no topic is configured (tests, CI).</summary>
    public bool RelayEnabled { get; set; } = true;

    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>The SNS adapter: the message ID and type travel as message attributes; the body is the event JSON.</summary>
public sealed class SnsEventPublisher(IAmazonSimpleNotificationService sns, IOptions<MessagingOptions> options) : IEventPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        // Called inside the relay's transaction on purpose (the documented exception to "no external call inside a
        // transaction", C8 Lab 4): the row lock is what stops a second relay publishing the same row. A crash between
        // this call and the commit publishes the row again later: a duplicate, never a loss (consumers deduplicate).
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = options.Value.TopicArn,
            Message = message.Payload,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["type"] = new() { DataType = "String", StringValue = message.Type },
                ["message_id"] = new() { DataType = "String", StringValue = message.Id.ToString() },
                ["tenant_id"] = new() { DataType = "String", StringValue = message.TenantId.ToString() },
            },
        }, cancellationToken);
    }
}

/// <summary>
/// C9 Lab 2: relays one module's outbox. Each pass, in one transaction under the execution strategy: select up to 50
/// unpublished rows oldest first <c>FOR UPDATE SKIP LOCKED</c>, publish each, mark them published, commit. A second relay
/// (another API instance) skips the locked rows instead of waiting for them or publishing them again.
/// </summary>
public sealed partial class OutboxRelay<TContext>(IServiceScopeFactory scopes, IOptions<MessagingOptions> options, TimeProvider time, ILogger<OutboxRelay<TContext>> logger)
    : BackgroundService
    where TContext : DbContext
{
    public const int BatchSize = 50;

    /// <summary>One pass: the number of rows published (0 when there was nothing to do).</summary>
    public async Task<int> RelayOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var schema = db.Model.GetDefaultSchema() ?? throw new InvalidOperationException($"{typeof(TContext).Name} has no default schema.");
        // The schema comes from the model (a constant per module), never from input, so building the text is safe.
        var select = "SELECT * FROM " + schema + "." + OutboxModel.Table
            + " WHERE published_at IS NULL ORDER BY occurred_at LIMIT " + BatchSize + " FOR UPDATE SKIP LOCKED";
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var batch = await db.Set<OutboxMessage>()
                .FromSqlRaw(select)
                .ToListAsync(ct);
            foreach (var message in batch)
            {
                message.Attempts++;
                await publisher.PublishAsync(message, ct);
                message.PublishedAt = time.GetUtcNow();
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return batch.Count;
        }, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RelayEnabled || string.IsNullOrEmpty(options.Value.TopicArn))
        {
            return; // tests and CI: no topic, no relay (tests run RelayOnceAsync themselves)
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Keep going while there is a backlog; rest between empty passes.
                while (await RelayOnceAsync(stoppingToken) == BatchSize)
                {
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                RelayFailed(logger, e, typeof(TContext).Name);
            }

            await Task.Delay(options.Value.RelayInterval, time, stoppingToken);
        }
    }

    [LoggerMessage(EventId = 1601, Level = LogLevel.Warning, Message = "Outbox relay for {Context} failed; the batch stays unpublished and is retried")]
    private static partial void RelayFailed(ILogger logger, Exception exception, string context);
}
