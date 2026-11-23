using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using HomeBuild.Domain;

namespace HomeBuild.Notifications.Lambda;

/// <summary>What the buyer reads in My Home: one JSON object per event, written once per message ID.</summary>
public sealed record BuyerNotification(
    Guid MessageId, string Type, Guid TenantId, Guid UnitId, Guid? ReservationId, Guid? BuyerId, string Text, DateTimeOffset CreatedAt, int? Version);

/// <summary>Port: where notifications are written (S3 in the function, in memory in tests).</summary>
public interface INotificationStore
{
    Task PutAsync(string key, byte[] json, CancellationToken cancellationToken);

    /// <summary>C10 Lab 6: creates the object only if it does not exist; false if another delivery got there first.</summary>
    Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public sealed class S3NotificationStore(IAmazonS3 s3, string bucket) : INotificationStore
{
    public Task PutAsync(string key, byte[] json, CancellationToken cancellationToken) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(json), ContentType = "application/json" }, cancellationToken);

    public async Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            // S3 conditional write: If-None-Match: * fails with 412 when the key exists.
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "{}", ContentType = "application/json", IfNoneMatch = "*" }, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => s3.DeleteObjectAsync(bucket, key, cancellationToken);
}

/// <summary>
/// C9 Lab 7: the notifications consumer on Lambda. The SQS event source hands it up to 10 messages; each becomes an
/// object at <c>notifications/{tenantId}/{buyerId}/{messageId}.json</c> (events that name no buyer, such as a
/// milestone, go under <c>notifications/{tenantId}/units/{unitId}/</c>). The key is the outbox message ID, so a
/// duplicate overwrites the same object: idempotent without a database. A message that fails is reported in the
/// partial batch response, so only it is retried; with partial responses off the whole batch fails and is retried.
/// </summary>
/// <remarks>
/// C10 Lab 6: SnagReminderDue is an email through SES, not a stored notification. A claim object at
/// <c>emails/{messageId}</c> (an S3 conditional write) makes a redelivered message send nothing; a failed send releases
/// the claim so the retry sends it.
/// </remarks>
public sealed class NotificationsFunction(INotificationStore store, TimeProvider time, bool partialBatchResponses, IEmailSender? email = null, EmailRouting? routing = null)
{
    public async Task<SQSBatchResponse> HandleAsync(SQSEvent sqsEvent, ILambdaContext context)
    {
        ArgumentNullException.ThrowIfNull(sqsEvent);
        ArgumentNullException.ThrowIfNull(context);
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        foreach (var record in sqsEvent.Records ?? [])
        {
            try
            {
                var key = await HandleOneAsync(record, CancellationToken.None);
                context.Logger.LogInformation($"processed {record.MessageId} (receive {Attribute(record.Attributes, "ApproximateReceiveCount")}) -> {key}");
            }
#pragma warning disable CA1031 // one bad message must not fail the others: it is reported and retried alone
            catch (Exception e)
#pragma warning restore CA1031
            {
                context.Logger.LogError($"failed {record.MessageId} (receive {Attribute(record.Attributes, "ApproximateReceiveCount")}): {e.GetType().Name}: {e.Message}");
                if (!partialBatchResponses)
                {
                    throw; // the Break: no partial response, so the whole batch is retried
                }

                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }
        }

        return new SQSBatchResponse(failures);
    }

    internal async Task<string> HandleOneAsync(SQSEvent.SQSMessage record, CancellationToken cancellationToken)
    {
        var type = record.MessageAttributes?.GetValueOrDefault("type")?.StringValue;
        var messageId = Guid.TryParse(record.MessageAttributes?.GetValueOrDefault("message_id")?.StringValue, out var id) ? id
            : throw new InvalidOperationException($"Message {record.MessageId} has no message_id attribute.");
        var now = time.GetUtcNow();
        if (type == IntegrationEvents.SnagReminderDue)
        {
            return await EmailAsync(messageId, Read(record.Body, MessageJson.Default.SnagReminderMessage), cancellationToken);
        }

        var notification = type switch
        {
            IntegrationEvents.MilestoneCompleted when Read(record.Body, MessageJson.Default.MilestoneCompletedMessage) is var m =>
                new BuyerNotification(messageId, type, m.TenantId, m.UnitId, null, null,
                    string.Create(CultureInfo.InvariantCulture, $"Good news: your home has reached a new stage ({m.Milestone}) on {m.CompletedAt:d MMMM yyyy}."), now, m.Version),
            IntegrationEvents.ContractIssued when Read(record.Body, MessageJson.Default.ContractIssuedMessage) is var c =>
                new BuyerNotification(messageId, type, c.TenantId, c.UnitId, c.ReservationId, null, "Your contract pack is ready to review in My Home.", now, null),
            // C10 Lab 1: extras the buyer can now choose (a unit event: no buyer, so stored under the unit).
            IntegrationEvents.ExtrasUnlocked when Read(record.Body, MessageJson.Default.ExtrasUnlockedMessage) is var x =>
                new BuyerNotification(messageId, type, x.TenantId, x.UnitId, null, null,
                    "New extras are open for your home: " + string.Join(", ", x.Extras.Select(e => e.Name)) + ". Choose them in My Home before they close.", now, x.Version),
            IntegrationEvents.ReservationExpired when Read(record.Body, MessageJson.Default.ReservationExpiredMessage) is var e =>
                new BuyerNotification(messageId, type, e.TenantId, e.UnitId, e.ReservationId, e.BuyerId, "Your reservation hold has ended because the deposit was not paid in time.", now, null),
            _ => throw new InvalidOperationException($"Message {record.MessageId} has type '{type}', which notifications do not handle."),
        };

        var key = KeyFor(notification);
        await store.PutAsync(key, JsonSerializer.SerializeToUtf8Bytes(notification, MessageJson.Default.BuyerNotification), cancellationToken);
        return key;
    }

    private async Task<string> EmailAsync(Guid messageId, SnagReminderMessage reminder, CancellationToken cancellationToken)
    {
        var to = (routing ?? throw new InvalidOperationException("Email is not configured (no routing).")).For(reminder.Kind)
            ?? throw new InvalidOperationException($"No recipient is configured for {reminder.Kind}.");
        var sender = email ?? throw new InvalidOperationException("Email is not configured (no sender).");
        var claim = $"emails/{reminder.TenantId}/{messageId}.json";
        if (!await store.TryClaimAsync(claim, cancellationToken))
        {
            return $"{claim} (already sent)";
        }

        try
        {
            var sesId = await sender.SendAsync(SnagEmailTemplates.For(reminder, to), cancellationToken);
            return $"{claim} ({reminder.Kind} email {sesId})"; // the provider's ID, never the address
        }
        catch
        {
            await store.DeleteAsync(claim, cancellationToken);
            throw;
        }
    }

    public static string KeyFor(BuyerNotification n) => n.BuyerId is { } buyer
        ? $"notifications/{n.TenantId}/{buyer}/{n.MessageId}.json"
        : $"notifications/{n.TenantId}/units/{n.UnitId}/{n.MessageId}.json";

    private static T Read<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(body, type) ?? throw new InvalidOperationException("Empty message body.");

    private static string Attribute(Dictionary<string, string>? attributes, string name) => attributes?.GetValueOrDefault(name) ?? "?";
}

/// <summary>Source-generated JSON for the messages and the notification (Native AOT: no reflection).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(MilestoneCompletedMessage))]
[JsonSerializable(typeof(ContractIssuedMessage))]
[JsonSerializable(typeof(ReservationExpiredMessage))]
[JsonSerializable(typeof(ExtrasUnlockedMessage))]
[JsonSerializable(typeof(BuyerNotification))]
[JsonSerializable(typeof(SnagReminderMessage))]
public sealed partial class MessageJson : JsonSerializerContext;

/// <summary>Source-generated JSON for the Lambda event and response.</summary>
[JsonSerializable(typeof(SQSEvent))]
[JsonSerializable(typeof(SQSBatchResponse))]
public sealed partial class LambdaJson : JsonSerializerContext;
