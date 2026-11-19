using System.Threading.Channels;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeBuild.Messaging;

/// <summary>A received message: SQS's handle for deleting it, the body and the attributes HomeBuild sends.</summary>
public sealed record QueueMessage(string ProviderId, string ReceiptHandle, string Body, IReadOnlyDictionary<string, string> Attributes, int ReceiveCount)
{
    public string? Type => Attributes.GetValueOrDefault("type");

    /// <summary>The outbox row's ID (Lab 2): the same across the relay's duplicates.</summary>
    public Guid? MessageId => Guid.TryParse(Attributes.GetValueOrDefault("message_id"), out var id) ? id : null;
}

/// <summary>Port: a queue (SQS in the hosts, an in-memory queue in tests and CI).</summary>
public interface IQueueClient
{
    Task<IReadOnlyList<QueueMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken);

    Task DeleteAsync(QueueMessage message, CancellationToken cancellationToken);

    /// <summary>Makes a message visible again now (visibility 0), for one taken but not started.</summary>
    Task ReleaseAsync(QueueMessage message, CancellationToken cancellationToken);

    /// <summary>Sets how long from now the message stays hidden (SQS ChangeMessageVisibility): to extend a long job, or to back off a retry.</summary>
    Task ChangeVisibilityAsync(QueueMessage message, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>The approximate number of visible messages (SQS ApproximateNumberOfMessages), for the dead-letter check.</summary>
    Task<int> DepthAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A failure that no retry can fix (bad data, an unknown unit): the consumer does not back off, so the message reaches
/// the dead-letter queue after maxReceiveCount receives in seconds instead of hours. Anything else is treated as transient.
/// </summary>
public sealed class PermanentMessageException : Exception
{
    public PermanentMessageException()
    {
    }

    public PermanentMessageException(string message) : base(message)
    {
    }

    public PermanentMessageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Port: what a consumer does with one message, in its own DI scope. Throwing leaves the message on the queue.</summary>
public interface IMessageHandler
{
    Task HandleAsync(QueueMessage message, CancellationToken cancellationToken);
}

public sealed class ConsumerOptions
{
    /// <summary>How many messages are processed at once: the cap on database connections this consumer uses.</summary>
    public int Processors { get; set; } = 4;

    /// <summary>The bounded channel between receiving and processing: received but not yet started.</summary>
    public int ChannelCapacity { get; set; } = 20;

    public int MaxMessages { get; set; } = 10;

    public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>The queue's visibility timeout (MessagingStack): a job still running at half of it extends it by a full one.</summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Extend visibility while a handler runs (C9 Lab 4). Off only to show the Break: a slow job handed to a second worker.</summary>
    public bool ExtendVisibility { get; set; } = true;

    /// <summary>The first retry's delay after a transient failure; it doubles with each receive, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The delay before receive <paramref name="receiveCount"/> + 1: RetryDelay × 2^(receiveCount − 1), capped.</summary>
    public TimeSpan BackoffFor(int receiveCount)
    {
        var factor = Math.Pow(2, Math.Clamp(receiveCount, 1, 20) - 1);
        return TimeSpan.FromTicks((long)Math.Min(RetryDelay.Ticks * factor, MaxRetryDelay.Ticks));
    }
}

public sealed class SqsQueueClient(IAmazonSQS sqs, string queueUrl) : IQueueClient
{
    public async Task<IReadOnlyList<QueueMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
    {
        var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = maxMessages,
            WaitTimeSeconds = (int)wait.TotalSeconds, // long polling
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = ["ApproximateReceiveCount"],
        }, cancellationToken);
        return [.. (response.Messages ?? []).Select(m => new QueueMessage(
            m.MessageId, m.ReceiptHandle, m.Body,
            (m.MessageAttributes ?? []).ToDictionary(a => a.Key, a => a.Value.StringValue),
            m.Attributes is { } a && a.TryGetValue("ApproximateReceiveCount", out var count) ? int.Parse(count, System.Globalization.CultureInfo.InvariantCulture) : 1))];
    }

    public Task DeleteAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, cancellationToken);
    }

    public Task ReleaseAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return sqs.ChangeMessageVisibilityAsync(queueUrl, message.ReceiptHandle, 0, cancellationToken);
    }

    public Task ChangeVisibilityAsync(QueueMessage message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return sqs.ChangeMessageVisibilityAsync(queueUrl, message.ReceiptHandle, (int)Math.Ceiling(timeout.TotalSeconds), cancellationToken);
    }

    public async Task<int> DepthAsync(CancellationToken cancellationToken)
    {
        var attributes = await sqs.GetQueueAttributesAsync(queueUrl, ["ApproximateNumberOfMessages"], cancellationToken);
        return attributes.ApproximateNumberOfMessages;
    }
}

/// <summary>
/// C9 Lab 3: one queue's consumer. A receive loop long-polls the queue and writes into a bounded <see cref="Channel{T}"/>
/// (it waits when the channel is full, so a burst cannot fill memory); a fixed number of processors read from it, each
/// message in its own DI scope (so at most <see cref="ConsumerOptions.Processors"/> database connections). A message is
/// deleted only after its handler returns (after the handler's transaction committed). On shutdown: receiving stops,
/// processors finish the message they hold, and messages still in the channel are released to the queue at once.
/// Lab 4: while a handler runs, its message's visibility is extended at half the timeout, so a slow job is never handed
/// to a second worker. A transient failure backs off by the receive count (the message reappears later each time); a
/// <see cref="PermanentMessageException"/> releases it at once. Either way the queue's redrive policy moves it to the
/// dead-letter queue after maxReceiveCount receives, and the other messages keep flowing meanwhile.
/// </summary>
public sealed partial class QueueConsumer(string name, IQueueClient queue, IServiceScopeFactory scopes, ConsumerOptions options, ILogger<QueueConsumer> logger)
    : BackgroundService
{
    private int _inFlight;
    private int _maxInFlight;
    private int _maxQueued;

    public string Name => name;

    /// <summary>The most messages processed at once so far (the burst test checks it).</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>The most messages waiting in the channel at once so far.</summary>
    public int MaxQueued => Volatile.Read(ref _maxQueued);

    private int _processed;

    public int Processed => Volatile.Read(ref _processed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = Channel.CreateBounded<QueueMessage>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
        });
        var processors = Enumerable.Range(0, options.Processors).Select(_ => ProcessAsync(channel.Reader, stoppingToken)).ToList();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Not cancelled by shutdown: cancelling a long poll only abandons it on this side, and SQS still hands
                // the poll whatever arrives before its wait ends, hidden for the visibility timeout (C9 Lab 3's burst:
                // 10 messages lost for 5 minutes). The wait bounds it (20 s, inside the host's shutdown timeout).
                var batch = await queue.ReceiveAsync(options.MaxMessages, options.Wait, CancellationToken.None);
                if (stoppingToken.IsCancellationRequested)
                {
                    foreach (var unstarted in batch)
                    {
                        await queue.ReleaseAsync(unstarted, CancellationToken.None);
                    }

                    break;
                }

                for (var i = 0; i < batch.Count; i++)
                {
                    try
                    {
                        await channel.Writer.WriteAsync(batch[i], stoppingToken);
                        Track(ref _maxQueued, channel.Reader.Count);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Shutting down with part of the batch not in the channel: give it back now.
                        foreach (var unstarted in batch.Skip(i))
                        {
                            await queue.ReleaseAsync(unstarted, CancellationToken.None);
                        }

                        break;
                    }
                }
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            await Task.WhenAll(processors);
            // Taken from the queue but never started: visible again now, not after the visibility timeout.
            while (channel.Reader.TryRead(out var unstarted))
            {
                await queue.ReleaseAsync(unstarted, CancellationToken.None);
            }
        }
    }

    private async Task ProcessAsync(ChannelReader<QueueMessage> reader, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (stoppingToken.IsCancellationRequested || !reader.TryRead(out var message))
            {
                continue;
            }

            Track(ref _maxInFlight, Interlocked.Increment(ref _inFlight));
            try
            {
                // In-flight work is not cancelled by shutdown: it finishes within the host's shutdown timeout.
                using var heartbeat = new CancellationTokenSource();
                var extending = options.ExtendVisibility ? ExtendWhileRunningAsync(message, heartbeat.Token) : Task.CompletedTask;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredKeyedService<IMessageHandler>(name).HandleAsync(message, CancellationToken.None);
                }
                finally
                {
                    await heartbeat.CancelAsync();
                    await extending;
                }

                await queue.DeleteAsync(message, CancellationToken.None);
                Interlocked.Increment(ref _processed);
            }
            catch (PermanentMessageException e)
            {
                // No retry can fix it: visible again now, so the redrive policy parks it after maxReceiveCount receives.
                PermanentFailure(logger, e, name, message.ProviderId, message.ReceiveCount);
                await TryAsync(() => queue.ReleaseAsync(message, CancellationToken.None));
            }
#pragma warning disable CA1031 // any other failure is treated as transient: the message is retried after a back-off
            catch (Exception e)
#pragma warning restore CA1031
            {
                var delay = options.BackoffFor(message.ReceiveCount);
                HandlerFailed(logger, e, name, message.ProviderId, message.ReceiveCount, delay);
                await TryAsync(() => queue.ChangeVisibilityAsync(message, delay, CancellationToken.None));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>Heartbeat: at half the visibility timeout, hide the message for another full timeout, until the handler ends.</summary>
    private async Task ExtendWhileRunningAsync(QueueMessage message, CancellationToken done)
    {
        var interval = options.VisibilityTimeout / 2;
        try
        {
            while (true)
            {
                await Task.Delay(interval, done);
                await queue.ChangeVisibilityAsync(message, options.VisibilityTimeout, CancellationToken.None);
                VisibilityExtended(logger, name, message.ProviderId, options.VisibilityTimeout);
            }
        }
        catch (OperationCanceledException) when (done.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // a failed extension must not fail the job; at worst the message is delivered again (the inbox absorbs it)
        catch (Exception e)
#pragma warning restore CA1031
        {
            ExtendFailed(logger, e, name, message.ProviderId);
        }
    }

    private async Task TryAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
#pragma warning disable CA1031 // if the queue call fails, the visibility timeout still returns the message
        catch (Exception e)
#pragma warning restore CA1031
        {
            ExtendFailed(logger, e, name, "(retry)");
        }
    }

    private static void Track(ref int max, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, value, current) != current)
        {
        }
    }

    [LoggerMessage(EventId = 1701, Level = LogLevel.Warning, Message = "Consumer {Consumer} failed message {MessageId} (receive {ReceiveCount}); retry in {Delay}")]
    private static partial void HandlerFailed(ILogger logger, Exception exception, string consumer, string messageId, int receiveCount, TimeSpan delay);

    [LoggerMessage(EventId = 1702, Level = LogLevel.Error, Message = "Consumer {Consumer} cannot process message {MessageId} (receive {ReceiveCount}): permanent failure, released for the dead-letter queue")]
    private static partial void PermanentFailure(ILogger logger, Exception exception, string consumer, string messageId, int receiveCount);

    [LoggerMessage(EventId = 1703, Level = LogLevel.Debug, Message = "Consumer {Consumer} extended message {MessageId} by {Timeout}")]
    private static partial void VisibilityExtended(ILogger logger, string consumer, string messageId, TimeSpan timeout);

    [LoggerMessage(EventId = 1704, Level = LogLevel.Warning, Message = "Consumer {Consumer} could not change visibility of message {MessageId}")]
    private static partial void ExtendFailed(ILogger logger, Exception exception, string consumer, string messageId);
}
