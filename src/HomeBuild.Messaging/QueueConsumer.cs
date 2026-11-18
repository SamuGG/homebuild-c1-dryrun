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
}

/// <summary>
/// C9 Lab 3: one queue's consumer. A receive loop long-polls the queue and writes into a bounded <see cref="Channel{T}"/>
/// (it waits when the channel is full, so a burst cannot fill memory); a fixed number of processors read from it, each
/// message in its own DI scope (so at most <see cref="ConsumerOptions.Processors"/> database connections). A message is
/// deleted only after its handler returns (after the handler's transaction committed). On shutdown: receiving stops,
/// processors finish the message they hold, and messages still in the channel are released to the queue at once.
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

    public int Processed { get; private set; }

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
                IReadOnlyList<QueueMessage> batch;
                try
                {
                    batch = await queue.ReceiveAsync(options.MaxMessages, options.Wait, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
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
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredKeyedService<IMessageHandler>(name).HandleAsync(message, CancellationToken.None);
                await queue.DeleteAsync(message, CancellationToken.None);
                Processed++;
            }
#pragma warning disable CA1031 // any failure leaves the message on the queue: it becomes visible again and is retried
            catch (Exception e)
#pragma warning restore CA1031
            {
                HandlerFailed(logger, e, name, message.ProviderId, message.ReceiveCount);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private static void Track(ref int max, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, value, current) != current)
        {
        }
    }

    [LoggerMessage(EventId = 1701, Level = LogLevel.Warning, Message = "Consumer {Consumer} failed message {MessageId} (receive {ReceiveCount}); left on the queue")]
    private static partial void HandlerFailed(ILogger logger, Exception exception, string consumer, string messageId, int receiveCount);
}
