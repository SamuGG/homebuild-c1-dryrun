using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using HomeBuild.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBuild.Workers.Tests;

/// <summary>
/// C9 Lab 5's FIFO comparison, opt-in (HOMEBUILD_AWS_TESTS=1, AWS_PROFILE, and the HomeBuild-OrderingLab stack deployed
/// with -c orderingLab=true): the same consumer (QueueConsumer, a 50 ms handler) against a standard queue and a FIFO queue
/// with a message group per unit. Each test prints the rows of the comparison table. CI never sets the variable.
/// </summary>
public sealed class FifoComparisonTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";
    private static readonly TimeSpan Work = TimeSpan.FromMilliseconds(50);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Row(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    // 200 messages: 10 units × versions 1–20 (or one group of 200), sent in version order, then drained.
    [Fact]
    public async Task Throughput_and_order()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1 and deploy HomeBuild-OrderingLab.");
        using var sqs = new AmazonSQSClient(RegionEndpoint.EUWest2);
        Row("| Queue | Groups | Receive batch | Processors | Messages | Time | Messages/s | Out of order per unit |");
        Row("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var (fifo, groups, batch) in new[] { (false, 10, 10), (true, 10, 10), (true, 10, 1), (true, 1, 1) })
        {
            var url = await UrlAsync(sqs, fifo);
            var sent = Enumerable.Range(1, 200 / groups).SelectMany(v => Enumerable.Range(0, groups).Select(g => new Body($"unit-{g}", v, false))).ToList();
            await SendAsync(sqs, url, fifo, sent);
            var run = await DrainAsync(sqs, url, new ConsumerOptions { Processors = 4, MaxMessages = batch, Wait = TimeSpan.FromSeconds(2) }, sent.Count);
            Row($"| {(fifo ? "FIFO" : "Standard")} | {groups} | {batch} | 4 | {sent.Count} | {run.Elapsed.TotalSeconds:F1} s | {sent.Count / run.Elapsed.TotalSeconds:F1} | {run.OutOfOrder} |");
        }
    }

    // FIFO drops a second send with the same deduplication ID within 5 minutes; after the window it is delivered again.
    // A standard queue has no deduplication: both copies arrive (the inbox handles them).
    [Fact]
    public async Task Deduplication_window()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1 and deploy HomeBuild-OrderingLab.");
        using var sqs = new AmazonSQSClient(RegionEndpoint.EUWest2);
        var fifo = await UrlAsync(sqs, fifo: true);
        var standard = await UrlAsync(sqs, fifo: false);
        var id = Guid.NewGuid().ToString(); // the outbox message ID
        var clock = Stopwatch.StartNew();
        var body = JsonSerializer.Serialize(new Body("unit-dedup", 1, false));
        foreach (var delay in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(10) })
        {
            await Task.Delay(delay, Ct);
            var f = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = fifo, MessageBody = body, MessageGroupId = "unit-dedup", MessageDeduplicationId = id }, Ct);
            var s = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = standard, MessageBody = body }, Ct);
            Row($"t={clock.Elapsed.TotalSeconds:F0}s send: FIFO MessageId {f.MessageId} (sequence {f.SequenceNumber}); standard MessageId {s.MessageId}");
        }

        Row($"within the window: FIFO delivered {await ReceiveAllAsync(sqs, fifo)}, standard delivered {await ReceiveAllAsync(sqs, standard)} (2 sends each)");
        await Task.Delay(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10) - clock.Elapsed, Ct);
        var again = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = fifo, MessageBody = body, MessageGroupId = "unit-dedup", MessageDeduplicationId = id }, Ct);
        Row($"t={clock.Elapsed.TotalSeconds:F0}s same deduplication ID again: MessageId {again.MessageId}; FIFO delivered {await ReceiveAllAsync(sqs, fifo)}");
    }

    // A message that keeps failing (a bug: transient, so it backs off 1, 2, 4, 8 s) at the head of unit A's group.
    // FIFO: nothing else of unit A is delivered until it reaches the DLQ; unit B flows. Standard: neither waits.
    [Fact]
    public async Task Poison_message_blocks_its_group()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1 and deploy HomeBuild-OrderingLab.");
        using var sqs = new AmazonSQSClient(RegionEndpoint.EUWest2);
        Row("| Queue | Unit B (5 messages) done | Unit A's 2nd message processed | Poison parked (DLQ) |");
        Row("| --- | --- | --- | --- |");
        foreach (var fifo in new[] { true, false })
        {
            var url = await UrlAsync(sqs, fifo);
            List<Body> sent = [new("unit-A", 1, true), .. Enumerable.Range(2, 4).Select(v => new Body("unit-A", v, false)), .. Enumerable.Range(1, 5).Select(v => new Body("unit-B", v, false))];
            await SendAsync(sqs, url, fifo, sent);
            var options = new ConsumerOptions { Processors = 4, MaxMessages = 1, Wait = TimeSpan.FromSeconds(2), RetryDelay = TimeSpan.FromSeconds(1), VisibilityTimeout = TimeSpan.FromSeconds(30) };
            var dlq = (await sqs.GetQueueUrlAsync(fifo ? "homebuild-ordering-fifo-dlq.fifo" : "homebuild-ordering-standard-dlq", Ct)).QueueUrl;
            var run = await DrainAsync(sqs, url, options, sent.Count - 1, stopWhen: async () => (await DepthAsync(sqs, dlq)) == 1);
            var b = run.Handled.Where(h => h.Body.Unit == "unit-B").Max(h => h.At);
            var a2 = run.Handled.Single(h => h.Body is { Unit: "unit-A", Version: 2 }).At;
            Row($"| {(fifo ? "FIFO" : "Standard")} | {b.TotalSeconds:F1} s | {a2.TotalSeconds:F1} s | {run.Elapsed.TotalSeconds:F1} s |");
            Assert.Equal(1, await ReceiveAllAsync(sqs, dlq)); // the poison, deleted from the DLQ (discarded)
        }
    }

    private sealed record Body(string Unit, int Version, bool Poison);

    private sealed record Handled(Body Body, TimeSpan At);

    private sealed record Run(TimeSpan Elapsed, int OutOfOrder, IReadOnlyList<Handled> Handled);

    private static async Task<string> UrlAsync(AmazonSQSClient sqs, bool fifo) =>
        (await sqs.GetQueueUrlAsync(fifo ? "homebuild-ordering-fifo.fifo" : "homebuild-ordering-standard", Ct)).QueueUrl;

    private static async Task SendAsync(AmazonSQSClient sqs, string url, bool fifo, IReadOnlyList<Body> bodies)
    {
        foreach (var chunk in bodies.Chunk(10))
        {
            var response = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = url,
                Entries = [.. chunk.Select((b, i) => new SendMessageBatchRequestEntry
                {
                    Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MessageBody = JsonSerializer.Serialize(b),
                    MessageGroupId = fifo ? b.Unit : null,
                    MessageDeduplicationId = fifo ? Guid.NewGuid().ToString() : null,
                })],
            }, Ct);
            Assert.Empty(response.Failed ?? []);
        }
    }

    /// <summary>Runs a QueueConsumer until <paramref name="expected"/> messages were handled (and <paramref name="stopWhen"/> holds).</summary>
    private static async Task<Run> DrainAsync(AmazonSQSClient sqs, string url, ConsumerOptions options, int expected, Func<Task<bool>>? stopWhen = null)
    {
        var handled = new ConcurrentQueue<Handled>();
        var clock = new Stopwatch();
        var services = new ServiceCollection()
            .AddKeyedScoped<IMessageHandler>("ordering", (_, _) => new RecordingHandler(handled, clock))
            .BuildServiceProvider();
        await using var _ = services;
        using var consumer = new QueueConsumer("ordering", new SqsQueueClient(sqs, url), services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<QueueConsumer>.Instance);
        clock.Start();
        await consumer.StartAsync(Ct);
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (handled.Count < expected || (stopWhen is not null && !await stopWhen()))
        {
            Assert.True(DateTime.UtcNow < until, $"timed out: {handled.Count} of {expected}");
            await Task.Delay(100, Ct);
        }

        var elapsed = clock.Elapsed;
        await consumer.StopAsync(Ct);
        // Per unit, in completion order: a version below one already completed is out of order.
        var outOfOrder = 0;
        foreach (var unit in handled.GroupBy(h => h.Body.Unit))
        {
            var max = 0;
            foreach (var h in unit)
            {
                if (h.Body.Version < max)
                {
                    outOfOrder++;
                }
                else
                {
                    max = h.Body.Version;
                }
            }
        }
        return new Run(elapsed, outOfOrder, [.. handled]);
    }

    private static async Task<int> DepthAsync(AmazonSQSClient sqs, string url) =>
        (await sqs.GetQueueAttributesAsync(url, ["ApproximateNumberOfMessages"], Ct)).ApproximateNumberOfMessages;

    private static async Task<int> ReceiveAllAsync(AmazonSQSClient sqs, string url)
    {
        var count = 0;
        for (var empty = 0; empty < 2;)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 }, Ct);
            var messages = response.Messages ?? [];
            empty = messages.Count == 0 ? empty + 1 : 0;
            foreach (var m in messages)
            {
                count++;
                await sqs.DeleteMessageAsync(url, m.ReceiptHandle, Ct);
            }
        }

        return count;
    }

    /// <summary>The order the consumer completes messages in, per unit; a poison message always throws (transient).</summary>
    private sealed class RecordingHandler(ConcurrentQueue<Handled> handled, Stopwatch clock) : IMessageHandler
    {
        public async Task HandleAsync(QueueMessage message, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Deserialize<Body>(message.Body)!;
            await Task.Delay(Work, cancellationToken);
            if (body.Poison)
            {
                throw new InvalidOperationException($"bug: cannot handle {body.Unit} v{body.Version}");
            }

            handled.Enqueue(new Handled(body, clock.Elapsed));
        }
    }
}
