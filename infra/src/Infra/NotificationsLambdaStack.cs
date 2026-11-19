using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SQS;
using Constructs;

namespace Infra;

/// <summary>
/// C9 Lab 7: the notifications consumer on Lambda. .NET 11 has no managed runtime, so both functions run on the OS-only
/// runtime provided.al2023 (arm64) with an executable named bootstrap: <c>homebuild-notifications</c> (Native AOT) reads
/// homebuild-notifications through an event source mapping (batch 10, partial batch responses); <c>homebuild-notifications-jit</c>
/// is the same handler with the JIT, for the cold-start comparison only (no event source). Each function's log group is
/// declared with retention and DESTROY, so cdk destroy leaves none behind. Build the zips first:
/// scripts/build-notifications-lambda.sh. The Break deploys with <c>-c partialBatch=false</c>.
/// </summary>
#pragma warning disable CA1711 // CDK names stacks "...Stack" by convention
public sealed class NotificationsLambdaStack : Stack
#pragma warning restore CA1711
{
    public NotificationsLambdaStack(Construct scope, string id, IStackProps props, IBucket bucket, string artifacts, bool partialBatch) : base(scope, id, props)
    {
        // By name, not a cross-stack reference: the messaging stack stays unchanged.
        var queue = Queue.FromQueueArn(this, "NotificationsQueue", $"arn:{Aws.PARTITION}:sqs:{Region}:{Account}:homebuild-notifications");

        foreach (var (kind, name) in new[] { ("aot", "homebuild-notifications"), ("jit", "homebuild-notifications-jit") })
        {
            var logs = new LogGroup(this, $"{kind}Logs", new LogGroupProps
            {
                LogGroupName = $"/aws/lambda/{name}",
                Retention = RetentionDays.ONE_WEEK,
                RemovalPolicy = RemovalPolicy.DESTROY,
            });
            var role = new Role(this, $"{kind}Role", new RoleProps
            {
                RoleName = $"{name}-lambda",
                AssumedBy = new ServicePrincipal("lambda.amazonaws.com"),
                Description = $"{name}: write its logs, write notifications/ in the storage bucket" + (kind == "aot" ? ", read homebuild-notifications" : ""),
                ManagedPolicies = [ManagedPolicy.FromAwsManagedPolicyName("service-role/AWSLambdaBasicExecutionRole")],
            });
            var function = new Function(this, kind == "aot" ? "Function" : "JitFunction", new FunctionProps
            {
                FunctionName = name,
                Role = role,
                Description = kind == "aot" ? "HomeBuild buyer notifications (Native AOT)" : "HomeBuild buyer notifications (JIT, for the cold-start comparison)",
                Runtime = Runtime.PROVIDED_AL2023,
                Architecture = Architecture.ARM_64,
                Handler = "bootstrap",
                Code = Code.FromAsset(System.IO.Path.Combine(artifacts, $"notifications-{kind}.zip")),
                MemorySize = 256,
                Timeout = Duration.Seconds(10), // under the queue's 1-minute visibility timeout
                LogGroup = logs,
                Environment = new Dictionary<string, string>
                {
                    ["NOTIFICATIONS_BUCKET"] = bucket.BucketName,
                    ["PARTIAL_BATCH_RESPONSES"] = partialBatch ? "true" : "false",
                },
            });
            bucket.GrantPut(function, "notifications/*");

            if (kind == "aot")
            {
                function.AddEventSource(new SqsEventSource(queue, new SqsEventSourceProps
                {
                    BatchSize = 10,
                    ReportBatchItemFailures = partialBatch,
                }));
            }
        }
    }
}
