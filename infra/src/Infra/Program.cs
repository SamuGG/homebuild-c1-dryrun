using Amazon.CDK;
using Infra;

// HomeBuild's AWS infrastructure. Every resource gets project=homebuild and a lab tag, so the teardown query
// (docs/teardown.md) finds everything the labs create.
var app = new App();
HomeBuildStacks.Prepare(app);

// Account from the CLI profile at synth time (never hard-coded: the repository is public); Region is fixed.
var env = HomeBuildStacks.Env(System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"));

_ = new FoundationStack(app, "HomeBuild-Foundation", new StackProps { Env = env });

// C11 Lab 5: Identity, Storage and Messaging are built by HomeBuildStacks, which the Aspire AppHost uses too.
_ = HomeBuildStacks.Identity(app, env);
var storage = HomeBuildStacks.Storage(app, env);

// C9 Lab 1: the messaging stack (SNS topic, SQS queues with DLQs). Queues and topics are free when idle.
_ = HomeBuildStacks.Messaging(app, env);

// C9 Lab 6: scheduled work (EventBridge Scheduler group, recurring schedules, the queue they send to).
var scheduling = new SchedulingStack(app, "HomeBuild-Scheduling", new StackProps { Env = env });
Tags.Of(scheduling).Add("lab", "c9-lab6");

// C9 Lab 7: the notifications Lambda (destroyed after the lab; C10 Lab 6 recreates it to send email). Needs the zips
// from scripts/build-notifications-lambda.sh, so it is synthesized only with -c notificationsLambda=true.
if (app.Node.TryGetContext("notificationsLambda") is not "true")
{
    HomeBuildStacks.KeepNotificationsOutputs(storage); // C11 Lab 5: keep the outputs the Notifications stack reads
}

if (app.Node.TryGetContext("notificationsLambda") is "true")
{
    var notifications = new NotificationsLambdaStack(app, "HomeBuild-Notifications", new StackProps { Env = env }, storage.Bucket,
        artifacts: System.IO.Path.GetFullPath("../artifacts"), partialBatch: app.Node.TryGetContext("partialBatch") is not "false",
        batchingWindowSeconds: int.TryParse(app.Node.TryGetContext("batchingWindow") as string, out var window) ? window : null,
        withJit: app.Node.TryGetContext("jit") is "true",
        // C10 Lab 6: SES addresses, given at deploy time (never committed): -c emailFrom=… -c aftercareEmail=… -c buyerEmail=…
        email: app.Node.TryGetContext("emailFrom") is string from ? new NotificationEmail(from, app.Node.TryGetContext("aftercareEmail") as string ?? from, app.Node.TryGetContext("buyerEmail") as string ?? from) : null);
    // Built in C9 Lab 7, destroyed, redeployed in C10 Lab 1 (ExtrasUnlocked; Lab 6 adds email).
    Tags.Of(notifications).Add("lab", "c10-lab1");
}

// C9 Lab 5: the FIFO comparison, deployed only for the measurement (-c orderingLab=true) and destroyed after it.
if (app.Node.TryGetContext("orderingLab") is "true")
{
    var ordering = new OrderingLabStack(app, "HomeBuild-OrderingLab", new StackProps { Env = env });
    Tags.Of(ordering).Add("lab", "c9-lab5");
}

app.Synth();
