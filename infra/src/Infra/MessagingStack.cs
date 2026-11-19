using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Amazon.CDK.AWS.SQS;
using Constructs;

namespace Infra;

/// <summary>
/// C9 Lab 1: one SNS topic fanned out to one SQS queue per consumer. Each subscription uses raw message delivery (the
/// queue gets the event JSON, not an SNS envelope) and a filter policy on the <c>type</c> message attribute, so a queue
/// receives only the events its consumer handles. Every queue has its own dead-letter queue (five receives, then park).
/// Queues and topics cost nothing while idle; cdk destroy deletes them (their default removal policy).
/// </summary>
#pragma warning disable CA1711 // CDK names stacks "...Stack" by convention
public sealed class MessagingStack : Stack
#pragma warning restore CA1711
{
    /// <summary>The event types each consumer handles: the filter policy of its subscription.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Consumers = new Dictionary<string, string[]>
    {
        // The contract worker (Lab 3): a paid deposit issues the contract pack.
        ["contracts"] = ["DepositPaid"],
        // Payments' worker: deposits (its own event, for follow-up work out of the request), stage payments due on a
        // milestone (C7's in-process handler moves here) and expiries (Lab 6 also sends ReservationExpiryDue directly).
        ["payments"] = ["DepositPaid", "MilestoneCompleted", "ReservationExpired"],
        // The buyer's messages (Lab 7's Lambda): progress, the contract, an expired hold.
        ["notifications"] = ["MilestoneCompleted", "ContractIssued", "ReservationExpired"],
        // C16's cache invalidation: availability changes when a hold ends.
        ["cache-invalidation"] = ["ReservationExpired"],
    };

    public MessagingStack(Construct scope, string id, IStackProps props) : base(scope, id, props)
    {
        Topic = new Topic(this, "Events", new TopicProps
        {
            TopicName = "homebuild-events",
            DisplayName = "HomeBuild domain events",
            EnforceSSL = true,
        });

        // The API publishes events (Lab 2's relay); it may do nothing else with the topic.
        var publisher = new Role(this, "ApiPublisher", new RoleProps
        {
            RoleName = "homebuild-api-events-publisher",
            AssumedBy = new AccountRootPrincipal(),
            Description = "HomeBuild API: publish domain events to homebuild-events, nothing else",
            MaxSessionDuration = Duration.Hours(1),
        });
        Topic.GrantPublish(publisher);

        foreach (var (name, types) in Consumers)
        {
            var dlq = new Queue(this, $"{Pascal(name)}Dlq", new QueueProps
            {
                QueueName = $"homebuild-{name}-dlq",
                // 14 days, the SQS maximum: a parked message waits for a person (Lab 4) through a fortnight's holidays.
                RetentionPeriod = Duration.Days(14),
                Encryption = QueueEncryption.SQS_MANAGED,
                EnforceSSL = true,
            });
            var queue = new Queue(this, Pascal(name), new QueueProps
            {
                QueueName = $"homebuild-{name}",
                // 4 days (the SQS default, stated): long enough to survive a worker down over a weekend, short enough
                // that a forgotten queue does not hoard stale events.
                RetentionPeriod = Duration.Days(4),
                // Above each consumer's worst-case processing time, so a message is not handed to a second worker
                // while the first is still on it (Lab 4 extends it for long jobs).
                VisibilityTimeout = VisibilityTimeouts[name],
                // Long polling by default (Lab 3's receive also asks for 20 s).
                ReceiveMessageWaitTime = Duration.Seconds(20),
                Encryption = QueueEncryption.SQS_MANAGED,
                EnforceSSL = true,
                DeadLetterQueue = new DeadLetterQueue { Queue = dlq, MaxReceiveCount = 5 },
            });

            // Only the topic may send: SqsSubscription adds the queue policy statement, limited to this topic's ARN.
            Topic.AddSubscription(new SqsSubscription(queue, new SqsSubscriptionProps
            {
                RawMessageDelivery = true,
                FilterPolicy = new Dictionary<string, SubscriptionFilter>
                {
                    ["type"] = SubscriptionFilter.StringFilter(new StringConditions { Allowlist = types }),
                },
            }));

            // The consumer's role: receive, delete and extend visibility on its own queue only.
            var worker = new Role(this, $"{Pascal(name)}Worker", new RoleProps
            {
                RoleName = $"homebuild-worker-{name}",
                AssumedBy = new AccountRootPrincipal(),
                Description = $"HomeBuild worker: consume homebuild-{name} only",
                MaxSessionDuration = Duration.Hours(1),
            });
            worker.AddToPolicy(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "ConsumeOwnQueue",
                Actions = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes"],
                Resources = [queue.QueueArn],
            }));
            // Lab 4: the scheduled dead-letter check reads its DLQ's depth (C14 replaces it with an alarm); no receive.
            worker.AddToPolicy(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "ReadOwnDlqDepth",
                Actions = ["sqs:GetQueueAttributes"],
                Resources = [dlq.QueueArn],
            }));
            Queues[name] = queue;
        }
    }

    public Topic Topic { get; }

    public Dictionary<string, Queue> Queues { get; } = [];

    // Per consumer: contract packs render a PDF and write to S3 (seconds), so 5 minutes; the others are a database
    // row or a small object (well under a second), so 1 minute.
    private static readonly Dictionary<string, Duration> VisibilityTimeouts = new()
    {
        ["contracts"] = Duration.Minutes(5),
        ["payments"] = Duration.Minutes(1),
        ["notifications"] = Duration.Minutes(1),
        ["cache-invalidation"] = Duration.Minutes(1),
    };

    private static string Pascal(string name) => string.Concat(name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}
