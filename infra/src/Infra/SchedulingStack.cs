using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Scheduler;
using Amazon.CDK.AWS.SQS;
using Constructs;

namespace Infra;

/// <summary>
/// C9 Lab 6: scheduled work. One queue (homebuild-scheduled, with its DLQ) receives what the schedules send; the API
/// consumes it. The schedule group <c>homebuild</c> holds Sales' one-time expiry schedules (created and deleted by the
/// API) and two recurring ones: the nightly reconciliation (C8 Lab 6) and the hourly clean-up. The scheduler role may
/// only send to that queue. Individual schedules cannot be tagged; the group is. Free tier: 14 million invocations a month.
/// </summary>
#pragma warning disable CA1711 // CDK names stacks "...Stack" by convention
public sealed class SchedulingStack : Stack
#pragma warning restore CA1711
{
    public const string GroupName = "homebuild";

    public SchedulingStack(Construct scope, string id, IStackProps props) : base(scope, id, props)
    {
        var dlq = new Queue(this, "ScheduledDlq", new QueueProps
        {
            QueueName = "homebuild-scheduled-dlq",
            RetentionPeriod = Duration.Days(14),
            Encryption = QueueEncryption.SQS_MANAGED,
            EnforceSSL = true,
        });
        var queue = new Queue(this, "Scheduled", new QueueProps
        {
            QueueName = "homebuild-scheduled",
            RetentionPeriod = Duration.Days(4),
            // The nightly reconciliation lists a day of Stripe sessions: minutes at most.
            VisibilityTimeout = Duration.Minutes(5),
            ReceiveMessageWaitTime = Duration.Seconds(20),
            Encryption = QueueEncryption.SQS_MANAGED,
            EnforceSSL = true,
            DeadLetterQueue = new DeadLetterQueue { Queue = dlq, MaxReceiveCount = 5 },
        });

        var group = new CfnScheduleGroup(this, "Group", new CfnScheduleGroupProps { Name = GroupName });

        // The schedules' role: send to homebuild-scheduled, nothing else; only this account's schedules may assume it.
        var scheduler = new Role(this, "Scheduler", new RoleProps
        {
            RoleName = "homebuild-scheduler",
            AssumedBy = new ServicePrincipal("scheduler.amazonaws.com", new ServicePrincipalOpts
            {
                Conditions = new Dictionary<string, object> { ["StringEquals"] = new Dictionary<string, object> { ["aws:SourceAccount"] = Account } },
            }),
            Description = "EventBridge Scheduler: send HomeBuild's scheduled messages to homebuild-scheduled only",
        });
        queue.GrantSendMessages(scheduler);

        Recurring("NightlyReconciliation", "homebuild-nightly-reconciliation", "cron(0 2 * * ? *)", "NightlyReconciliation", "Reconcile yesterday's Stripe sessions and refunds with HomeBuild's deposits (C8 Lab 6)");
        Recurring("HourlyCleanup", "homebuild-hourly-cleanup", "cron(0 * * * ? *)", "HourlyCleanup", "Delete expired idempotency keys and old published outbox rows; expire holds whose schedule never fired");

        // The API: create, move and delete expiry schedules in the group (passing the scheduler role), and consume the queue.
        var api = new Role(this, "ApiScheduling", new RoleProps
        {
            RoleName = "homebuild-api-scheduling",
            AssumedBy = new AccountRootPrincipal(),
            Description = "HomeBuild API: manage homebuild-expiry-* schedules and consume homebuild-scheduled",
            MaxSessionDuration = Duration.Hours(1),
        });
        api.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "ManageExpirySchedules",
            Actions = ["scheduler:CreateSchedule", "scheduler:UpdateSchedule", "scheduler:DeleteSchedule", "scheduler:GetSchedule"],
            Resources = [$"arn:{Aws.PARTITION}:scheduler:{Region}:{Account}:schedule/{GroupName}/homebuild-expiry-*"],
        }));
        api.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "PassSchedulerRole",
            Actions = ["iam:PassRole"],
            Resources = [scheduler.RoleArn],
            Conditions = new Dictionary<string, object> { ["StringEquals"] = new Dictionary<string, object> { ["iam:PassedToService"] = "scheduler.amazonaws.com" } },
        }));
        api.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "ConsumeScheduledQueue",
            Actions = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes"],
            Resources = [queue.QueueArn],
        }));

        _ = new CfnOutput(this, "QueueUrl", new CfnOutputProps { Value = queue.QueueUrl });
        _ = new CfnOutput(this, "SchedulerRoleName", new CfnOutputProps { Value = scheduler.RoleName });

        void Recurring(string id, string name, string cron, string type, string description)
        {
            var schedule = new CfnSchedule(this, id, new CfnScheduleProps
            {
                Name = name,
                GroupName = GroupName,
                Description = description,
                ScheduleExpression = cron,
                ScheduleExpressionTimezone = "Europe/London",
                FlexibleTimeWindow = new CfnSchedule.FlexibleTimeWindowProperty { Mode = "OFF" },
                State = "ENABLED",
                Target = new CfnSchedule.TargetProperty
                {
                    Arn = queue.QueueArn,
                    RoleArn = scheduler.RoleArn,
                    // The SQS target sends only a body (no message attributes): the type travels in it.
                    Input = $$"""{"type":"{{type}}","scheduledTime":"<aws.scheduler.scheduled-time>"}""",
                },
            });
            schedule.Node.AddDependency(group);
        }
    }
}
