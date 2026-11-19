using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using HomeBuild.Domain;

namespace HomeBuild.Notifications.Lambda.Tests;

/// <summary>C9 Lab 7's acceptance test and the handler's behaviour, without AWS.</summary>
public sealed class NotificationsFunctionTests
{
    private static readonly Guid Tenant = Guid.Parse("0198a000-0000-7000-8000-00000000000a");

    // One bad message in a batch of ten: only it is reported, so SQS retries only it; the nine are stored once.
    [Fact]
    public async Task Partial_batch_failure_retries_only_failed_message()
    {
        var store = new MemoryStore();
        var function = new NotificationsFunction(store, TimeProvider.System, partialBatchResponses: true);
        var batch = Enumerable.Range(1, 9).Select(v => Milestone(Guid.NewGuid(), v)).Append(Poison()).ToList();

        var response = await function.HandleAsync(new SQSEvent { Records = batch }, new Context());

        var failed = Assert.Single(response.BatchItemFailures);
        Assert.Equal(batch[^1].MessageId, failed.ItemIdentifier);
        Assert.Equal(9, store.Objects.Count);
    }

    // The Break: without partial responses the handler throws, so Lambda fails the whole batch and SQS redelivers all ten.
    [Fact]
    public async Task Without_partial_responses_one_failure_fails_the_batch()
    {
        var store = new MemoryStore();
        var function = new NotificationsFunction(store, TimeProvider.System, partialBatchResponses: false);
        var batch = Enumerable.Range(1, 9).Select(v => Milestone(Guid.NewGuid(), v)).Append(Poison()).ToList();

        await Assert.ThrowsAsync<JsonException>(() => function.HandleAsync(new SQSEvent { Records = batch }, new Context()));

        Assert.Equal(9, store.Puts); // done, but the batch fails: all nine will be processed again
    }

    // The key is the outbox message ID: a duplicate delivery overwrites the same object.
    [Fact]
    public async Task Duplicate_message_writes_the_same_object()
    {
        var store = new MemoryStore();
        var function = new NotificationsFunction(store, TimeProvider.System, partialBatchResponses: true);
        var unit = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await function.HandleAsync(new SQSEvent { Records = [Milestone(unit, 2, messageId), Milestone(unit, 2, messageId)] }, new Context());

        Assert.Equal(2, store.Puts);
        var (key, json) = Assert.Single(store.Objects);
        Assert.Equal($"notifications/{Tenant}/units/{unit}/{messageId}.json", key);
        var notification = JsonSerializer.Deserialize(json, MessageJson.Default.BuyerNotification)!;
        Assert.Equal(2, notification.Version);
        Assert.Contains("(Frame)", notification.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expiry_is_written_under_the_buyer()
    {
        var store = new MemoryStore();
        var function = new NotificationsFunction(store, TimeProvider.System, partialBatchResponses: true);
        var buyer = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new ReservationExpiredMessage(Guid.NewGuid(), Guid.NewGuid(), buyer, Tenant, DateTimeOffset.UtcNow), MessageJson.Default.ReservationExpiredMessage);

        var response = await function.HandleAsync(new SQSEvent { Records = [Record(IntegrationEvents.ReservationExpired, body, messageId)] }, new Context());

        Assert.Empty(response.BatchItemFailures);
        Assert.Equal($"notifications/{Tenant}/{buyer}/{messageId}.json", Assert.Single(store.Objects).Key);
    }

    private static readonly string[] Milestones = ["Foundations", "Frame", "Roof", "FirstFix", "Plastering", "SecondFix", "UtilitiesConnected", "PracticalCompletion", "Foundations"];

    private static SQSEvent.SQSMessage Milestone(Guid unit, int version, Guid? messageId = null) =>
        Record(IntegrationEvents.MilestoneCompleted,
            JsonSerializer.Serialize(new MilestoneCompletedMessage(unit, Tenant, Milestones[version - 1], new DateOnly(2026, 11, version), version), MessageJson.Default.MilestoneCompletedMessage),
            messageId ?? Guid.NewGuid());

    private static SQSEvent.SQSMessage Poison() => Record(IntegrationEvents.MilestoneCompleted, "{not json", Guid.NewGuid());

    private static SQSEvent.SQSMessage Record(string type, string body, Guid messageId) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        Body = body,
        Attributes = new Dictionary<string, string> { ["ApproximateReceiveCount"] = "1" },
        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>
        {
            ["type"] = new() { DataType = "String", StringValue = type },
            ["message_id"] = new() { DataType = "String", StringValue = messageId.ToString() },
        },
    };

    private sealed class MemoryStore : INotificationStore
    {
        private int _puts;

        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

        public int Puts => _puts;

        public Task PutAsync(string key, byte[] json, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _puts);
            Objects[key] = json;
            return Task.CompletedTask;
        }
    }

    private sealed class Context : ILambdaContext
    {
        public string AwsRequestId => "test";
        public IClientContext ClientContext => null!;
        public string FunctionName => "homebuild-notifications";
        public string FunctionVersion => "$LATEST";
        public ICognitoIdentity Identity => null!;
        public string InvokedFunctionArn => "arn:aws:lambda:eu-west-2:000000000000:function:homebuild-notifications";
        public ILambdaLogger Logger { get; } = new TestLog();
        public string LogGroupName => "/aws/lambda/homebuild-notifications";
        public string LogStreamName => "test";
        public int MemoryLimitInMB => 256;
        public TimeSpan RemainingTime => TimeSpan.FromSeconds(10);
    }

    private sealed class TestLog : ILambdaLogger
    {
        public void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

        public void LogLine(string message) => Log(message);
    }
}
