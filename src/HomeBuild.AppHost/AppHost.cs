// C11 Lab 4: HomeBuild for local development, in one command (`dotnet aspire run`). PostgreSQL 19 and Valkey run as
// persistent containers with data volumes (they survive AppHost restarts); the migrations resource applies C3's
// bundles before the API and the workers start; the API, the web app and the workers run as projects, wired through
// references. The dashboard shows their logs, traces and metrics.
var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL 19 (C11 Lab 0). The superuser owns the schemas and runs the migrations; the apps connect as homebuild_app,
// the row-level security role (C4), which the migrations create and scripts/migrate-local.sh gives a login with a
// local-only password (as compose.yaml's comment did).
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("19")
    .WithDataVolume("homebuild-pgdata-aspire")
    .WithLifetime(ContainerLifetime.Persistent);
var database = postgres.AddDatabase("homebuild");
var endpoint = postgres.Resource.PrimaryEndpoint;
var appConnection = ReferenceExpression.Create(
    $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database=homebuild;Username=homebuild_app;Password=homebuild-dev-app");

// Valkey: the web app's token store (IDistributedCache), which was in memory and lost every session on restart.
var cache = builder.AddValkey("cache")
    .WithDataVolume("homebuild-valkey-aspire")
    .WithLifetime(ContainerLifetime.Persistent);

// C3's migration bundles, built for this machine on first use (scripts/migrate-local.sh), as the superuser.
var migrations = builder.AddExecutable("migrations", "bash", "../..", "scripts/migrate-local.sh")
    .WithEnvironment("MIGRATIONS_CONNECTION", database)
    .WaitFor(database);

// Cognito (C4): the API trusts access tokens from the user pool the web app signs in with. Parameters live in the
// AppHost's user-secrets (Parameters:cognito-authority, Parameters:cognito-client-id); no values in the repository.
var cognitoAuthority = builder.AddParameter("cognito-authority");
var cognitoClientId = builder.AddParameter("cognito-client-id");

var api = builder.AddDotnetProject("api", "../HomeBuild.Api/HomeBuild.Api.csproj")
    .WithEnvironment("ConnectionStrings__HomeBuild", appConnection)
    .WithEnvironment("Authentication__Schemes__Bearer__Authority", cognitoAuthority)
    .WithEnvironment("Authentication__Schemes__Bearer__ValidIssuer", cognitoAuthority)
    .WithEnvironment("Tokens__AllowedClientIds__1", cognitoClientId)
    .WithHttpHealthCheck("/alive")
    .WaitForCompletion(migrations);

builder.AddDotnetProject("workers", "../HomeBuild.Workers/HomeBuild.Workers.csproj")
    .WithEnvironment("ConnectionStrings__HomeBuild", appConnection)
    .WaitForCompletion(migrations);

builder.AddDotnetProject("web", "../HomeBuild.Web/HomeBuild.Web.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(cache)
    .WithEnvironment("CatalogueApi__BaseAddress", ReferenceExpression.Create($"{api.GetEndpoint("http")}/v2/"))
    .WithHttpHealthCheck("/alive")
    .WaitFor(api)
    .WaitFor(cache);

builder.Build().Run();
