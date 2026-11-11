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

app.Synth();
