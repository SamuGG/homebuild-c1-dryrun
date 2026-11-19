using Amazon.CDK;
using Infra;

// HomeBuild's AWS infrastructure. Every resource gets project=homebuild and a lab tag, so the teardown query
// (docs/teardown.md) finds everything the labs create.
var app = new App();
Tags.Of(app).Add("project", "homebuild");
Tags.Of(app).Add("lab", app.Node.TryGetContext("lab") as string ?? "c4");

// Account from the CLI profile at synth time (never hard-coded: the repository is public); Region is fixed.
var env = new Amazon.CDK.Environment
{
    Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
    Region = "eu-west-2",
};

_ = new FoundationStack(app, "HomeBuild-Foundation", new StackProps { Env = env });

var identity = new IdentityStack(app, "HomeBuild-Identity", new StackProps { Env = env },
    domainPrefix: app.Node.TryGetContext("domainPrefix") as string ?? "homebuild-dryrun-dev");
Tags.Of(identity).Add("lab", "c4-lab2");

var storage = new StorageStack(app, "HomeBuild-Storage", new StackProps { Env = env }, webOrigins: ["http://localhost:5266"]);
Tags.Of(storage).Add("lab", "c5-lab7");

// C9 Lab 1: the messaging stack (SNS topic, SQS queues with DLQs). Queues and topics are free when idle.
var messaging = new MessagingStack(app, "HomeBuild-Messaging", new StackProps { Env = env });
Tags.Of(messaging).Add("lab", "c9");

// C9 Lab 6: scheduled work (EventBridge Scheduler group, recurring schedules, the queue they send to).
var scheduling = new SchedulingStack(app, "HomeBuild-Scheduling", new StackProps { Env = env });
Tags.Of(scheduling).Add("lab", "c9-lab6");

// C9 Lab 5: the FIFO comparison, deployed only for the measurement (-c orderingLab=true) and destroyed after it.
if (app.Node.TryGetContext("orderingLab") is "true")
{
    var ordering = new OrderingLabStack(app, "HomeBuild-OrderingLab", new StackProps { Env = env });
    Tags.Of(ordering).Add("lab", "c9-lab5");
}

app.Synth();
