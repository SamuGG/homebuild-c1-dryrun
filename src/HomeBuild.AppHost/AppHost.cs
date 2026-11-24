// C11 Lab 4: HomeBuild for local development, in one command (`dotnet aspire run`). PostgreSQL 19 and Valkey run as
// persistent containers with data volumes (they survive AppHost restarts); the migrations resource applies C3's
// bundles before the API and the workers start; the API, the web app and the workers run as projects, wired through
// references. The dashboard shows their logs, traces and metrics.
using Amazon;
using Amazon.CDK;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Infra;

var builder = DistributedApplication.CreateBuilder(args);

// C11 Lab 5: real AWS dev resources. The AppHost provisions or updates HomeBuild's Identity, Storage and Messaging
// stacks with the AWS CDK (the same factory as the CDK app, so the same templates) and hands their outputs to the
// projects as configuration. The profile comes from configuration (AWS:Profile; this account uses an IAM-user
// profile, not SSO), the Region is HomeBuild's. No keys anywhere.
var profile = builder.Configuration["AWS:Profile"] ?? throw new InvalidOperationException("Set AWS:Profile in the AppHost's user-secrets.");
var awsConfig = builder.AddAWSSDKConfig().WithProfile(profile).WithRegion(RegionEndpoint.GetBySystemName(HomeBuildStacks.Region));
var account = AccountOf(profile);
var env = HomeBuildStacks.Env(account);
var infra = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "infra"));
App Prepared(Constructs.Construct scope)
{
    var app = (App)scope;
    HomeBuildStacks.Prepare(app, Path.Combine(infra, "cdk.json"));
    return app;
}

var identity = builder.AddAWSCDKStack("identity", scope => HomeBuildStacks.Identity(Prepared(scope), env)).WithReference(awsConfig);
var storage = builder.AddAWSCDKStack("storage", scope =>
{
    var stack = HomeBuildStacks.Storage(Prepared(scope), env, Path.Combine(infra, "keys"));
    HomeBuildStacks.KeepNotificationsOutputs(stack); // HomeBuild-Notifications (C10) reads these; the AppHost doesn't own it
    return stack;
}).WithReference(awsConfig);
var messaging = builder.AddAWSCDKStack("messaging", scope => HomeBuildStacks.Messaging(Prepared(scope), env)).WithReference(awsConfig);

// PostgreSQL 19 (C11 Lab 0). The superuser owns the schemas and runs the migrations; the apps connect as homebuild_app,
// the row-level security role (C4), which the migrations create and scripts/migrate-local.sh gives a login with a
// local-only password (as compose.yaml's comment did).
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("19")
    .WithDataVolume("homebuild-pgdata-aspire")
    // C11 Lab 7 (ADR-0018): hard memory limits, so a runaway container fails visibly instead of swapping the laptop.
    .WithContainerRuntimeArgs("--memory", builder.Configuration["Limits:Postgres"] ?? "1g")
    .WithLifetime(ContainerLifetime.Persistent);
var database = postgres.AddDatabase("homebuild");
var endpoint = postgres.Resource.PrimaryEndpoint;
var appConnection = ReferenceExpression.Create(
    $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database=homebuild;Username=homebuild_app;Password=homebuild-dev-app");

// Valkey: the web app's token store (IDistributedCache), which was in memory and lost every session on restart.
var cache = builder.AddValkey("cache")
    .WithDataVolume("homebuild-valkey-aspire")
    .WithContainerRuntimeArgs("--memory", builder.Configuration["Limits:Valkey"] ?? "256m")
    .WithLifetime(ContainerLifetime.Persistent);

// C3's migration bundles, built for this machine on first use (scripts/migrate-local.sh), as the superuser.
var migrations = builder.AddExecutable("migrations", "bash", "../..", "scripts/migrate-local.sh")
    .WithEnvironment("MIGRATIONS_CONNECTION", database)
    .WaitFor(database);

var api = builder.AddDotnetProject("api", "../HomeBuild.Api/HomeBuild.Api.csproj")
    .WithEnvironment("ConnectionStrings__HomeBuild", appConnection)
    // Cognito (C4): the API trusts access tokens from the stack's user pool and web client.
    .WithEnvironment("Authentication__Schemes__Bearer__Authority", identity.GetOutput("Issuer"))
    .WithEnvironment("Authentication__Schemes__Bearer__ValidIssuer", identity.GetOutput("Issuer"))
    .WithEnvironment("Tokens__AllowedClientIds__1", identity.GetOutput("WebClientId"))
    // Storage (C5, C10 Lab 5) and messaging (C9, C10): bucket, upload role, photo CDN, topic and the API's queues.
    .WithEnvironment("Storage__BucketName", storage.GetOutput("BucketName"))
    .WithEnvironment("Storage__UploadRoleArn", storage.GetOutput("UploadRoleArn"))
    .WithEnvironment("Notifications__BucketName", storage.GetOutput("BucketName"))
    .WithEnvironment("CloudFront__Domain", storage.GetOutput("PhotosDomain"))
    .WithEnvironment("CloudFront__KeyPairId", storage.GetOutput("SnagPhotosKeyId"))
    .WithEnvironment("Messaging__TopicArn", messaging.GetOutput("TopicArn"))
    .WithEnvironment("Messaging__RelayEnabled", "true")
    .WithEnvironment("Extras__QueueUrl", messaging.GetOutput("ExtrasQueueUrl"))
    .WithEnvironment("ChangeOrders__QueueUrl", messaging.GetOutput("ChangeOrdersQueueUrl"))
    .WithReference(awsConfig)
    .WaitFor(identity).WaitFor(storage).WaitFor(messaging)
    .WithHttpHealthCheck("/alive")
    .WaitForCompletion(migrations);

var workers = builder.AddDotnetProject("workers", "../HomeBuild.Workers/HomeBuild.Workers.csproj")
    .WithEnvironment("ConnectionStrings__HomeBuild", appConnection)
    // C9 Lab 3: the contracts worker (its queue, the bucket for contract packs, the topic for ContractIssued).
    .WithEnvironment("Workers__Contracts__QueueUrl", messaging.GetOutput("ContractsQueueUrl"))
    .WithEnvironment("Storage__BucketName", storage.GetOutput("BucketName"))
    .WithEnvironment("Messaging__TopicArn", messaging.GetOutput("TopicArn"))
    .WithReference(awsConfig)
    .WaitFor(storage).WaitFor(messaging)
    .WaitForCompletion(migrations);

// C11 Lab 7 (ADR-0018): a 128 MB heap limit makes a runaway worker fail with OutOfMemoryException instead of
// growing into swap. Measured: server GC (DATAS, the default) committed 18 MB idle and workstation GC 25 MB, with
// RSS about 200 MB either way, so the GC mode stays the default; Limits:WorkersGcServer overrides it to compare.
if (builder.Configuration["Limits:WorkersGcServer"] is { Length: > 0 } gcServer)
    workers.WithEnvironment("DOTNET_gcServer", gcServer);
if (builder.Configuration["Limits:WorkersHeapHardLimit"] is not "")
    workers.WithEnvironment("DOTNET_GCHeapHardLimit", builder.Configuration["Limits:WorkersHeapHardLimit"] ?? "0x8000000");

builder.AddDotnetProject("web", "../HomeBuild.Web/HomeBuild.Web.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(cache)
    .WithEnvironment("CatalogueApi__BaseAddress", ReferenceExpression.Create($"{api.GetEndpoint("http")}/v2/"))
    // C4: sign-in with the stack's user pool and web client (the client secret stays in the web app's user-secrets).
    .WithEnvironment("Authentication__Cognito__Authority", identity.GetOutput("Issuer"))
    .WithEnvironment("Authentication__Cognito__ClientId", identity.GetOutput("WebClientId"))
    .WaitFor(identity)
    .WithHttpHealthCheck("/alive")
    .WaitFor(api)
    .WaitFor(cache);

builder.Build().Run();

// The account, for the stacks' environment (as CDK_DEFAULT_ACCOUNT is for the CDK CLI): from the profile, never written down.
static string AccountOf(string profile)
{
    if (!new CredentialProfileStoreChain().TryGetAWSCredentials(profile, out var credentials))
    {
        throw new InvalidOperationException($"AWS profile '{profile}' not found.");
    }

    using var sts = new AmazonSecurityTokenServiceClient(credentials, RegionEndpoint.GetBySystemName(HomeBuildStacks.Region));
    return sts.GetCallerIdentityAsync(new GetCallerIdentityRequest()).GetAwaiter().GetResult().Account;
}
