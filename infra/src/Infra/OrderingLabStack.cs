using Amazon.CDK;
using Amazon.CDK.AWS.SQS;
using Constructs;

namespace Infra;

/// <summary>
/// C9 Lab 5, measured then destroyed: a FIFO queue (one message group per unit) and a standard twin with the same
/// settings, each with its own dead-letter queue, for the ordering comparison. Created only with
/// <c>-c orderingLab=true</c>, so a later <c>cdk deploy --all</c> does not bring it back. Free while idle; cdk destroy
/// deletes the queues (their default removal policy).
/// </summary>
#pragma warning disable CA1711 // CDK names stacks "...Stack" by convention
public sealed class OrderingLabStack : Stack
#pragma warning restore CA1711
{
    public OrderingLabStack(Construct scope, string id, IStackProps props) : base(scope, id, props)
    {
        foreach (var fifo in new[] { true, false })
        {
            var name = fifo ? "homebuild-ordering-fifo" : "homebuild-ordering-standard";
            var suffix = fifo ? ".fifo" : "";
            var dlq = new Queue(this, fifo ? "FifoDlq" : "StandardDlq", new QueueProps
            {
                QueueName = $"{name}-dlq{suffix}",
                Fifo = fifo ? true : null,
                RetentionPeriod = Duration.Days(1),
                Encryption = QueueEncryption.SQS_MANAGED,
                EnforceSSL = true,
            });
            _ = new Queue(this, fifo ? "Fifo" : "Standard", new QueueProps
            {
                QueueName = name + suffix,
                Fifo = fifo ? true : null,
                // Explicit deduplication IDs (the outbox message ID), not a hash of the body.
                ContentBasedDeduplication = fifo ? false : null,
                RetentionPeriod = Duration.Days(1), // test messages only
                VisibilityTimeout = Duration.Seconds(30),
                ReceiveMessageWaitTime = Duration.Seconds(20),
                Encryption = QueueEncryption.SQS_MANAGED,
                EnforceSSL = true,
                DeadLetterQueue = new DeadLetterQueue { Queue = dlq, MaxReceiveCount = 5 },
            });
        }
    }
}
