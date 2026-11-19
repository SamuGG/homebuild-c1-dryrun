using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Configuration;
using HomeBuild.Api.Http;
using HomeBuild.Api.Http.Idempotency;
using HomeBuild.Api.Modules.Accounts;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Api.Modules.Extras;
using HomeBuild.Api.Modules.Notifications;

var builder = WebApplication.CreateBuilder(args);
// The API takes small JSON bodies only; Kestrel's default 30 MB limit let a 2 MB reservation body through
// (red-team finding, C4 Lab 6). Larger bodies get 413 before any handler reads them.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 64 * 1024);
// Secrets: user-secrets in Development (loaded by the default builder), SSM Parameter Store in AWS.
builder.AddHomeBuildSecrets();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole();
}

// Scope validation in every environment, not only Development: captive dependencies fail at startup.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

builder.Services.AddHomeBuildApiVersioning();
builder.Services.AddOpenApi("v1");
builder.Services.AddOpenApi("v2");
builder.Services.AddValidation();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context => Problems.AddTraceId(context.ProblemDetails, context.HttpContext));
builder.Services.AddExceptionHandler<LoggingExceptionHandler>();
builder.Services.AddHomeBuildRateLimiting();

builder.Services.AddHomeBuildAuthentication();
builder.Services.AddHomeBuildPolicies();
builder.Services.AddTenancy();
// Scoped: counts the request's open database transactions, so no external call happens inside one (C8 Lab 4).
builder.Services.AddScoped<HomeBuild.Api.Data.OpenTransactions>();
builder.Services.AddIdempotency();
// C9 Lab 2: relays for the modules' outboxes (they run only when Messaging:TopicArn is set).
HomeBuild.Api.Data.OutboxServices.AddOutboxRelays(builder.Services);
builder.Services.AddCatalogueModule();
builder.Services.AddSalesModule();
builder.Services.AddAccountsModule();
builder.Services.AddConstructionModule();
builder.Services.AddPaymentsModule();
builder.Services.AddExtrasModule();
builder.Services.AddNotificationsModule();
// C9 Lab 6: expiry schedules (EventBridge Scheduler) and the scheduled-work queue's consumer.
HomeBuild.Api.Scheduling.ScheduledWorkServices.AddScheduledWork(builder.Services, builder.Configuration);
var app = builder.Build();

// C8 Lab 6: `dotnet run --project src/HomeBuild.Api -- reconcile --from <date> --to <date>` runs the reconciliation
// report with the API's configuration and services, then exits (exit code 2 when there are mismatches).
if (args is ["reconcile", ..])
{
    Environment.ExitCode = await HomeBuild.Api.Modules.Payments.Reconcile.ReconcileCommand.RunFromCommandLineAsync(app.Services, args);
    return;
}


// Pipeline order, one reason per line:
// 1. Exception handler: first, so it wraps everything after it and turns any exception into a 500 ProblemDetails.
app.UseExceptionHandler();
// 2. Status code pages: gives empty error responses (404, 405, filter results) a ProblemDetails body; it must sit inside the exception handler.
app.UseStatusCodePages();
// 3. Routing: picks the endpoint, so middleware after it (rate limiter in Lab 7, auth in C4) can read endpoint metadata.
app.UseRouting();
// 4. Authentication: turns the bearer token into a user; after routing, before anything that decides by identity.
app.UseAuthentication();
// 5. Authorisation: checks the endpoint's policy (the fallback: signed in) and answers 401/403 before the handler runs.
app.UseAuthorization();
// 6. Rate limiter: after routing, so it can read each endpoint's policy; before the endpoint, so a rejected request costs nothing.
app.UseRateLimiter();
// 7. Tenant: from the tenant_id claim (claims transformation runs in UseAuthentication); after authorisation, so 401/403 come first.
app.UseMiddleware<TenantMiddleware>();
// 8. Idempotency keys (C8 Lab 3): after the tenant, so 400/401/403 answers are never stored; it needs the caller.
app.UseMiddleware<IdempotencyMiddleware>();

// The OpenAPI document is published only in Development and in CI, where Bruno and reviewers use it.
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("CI"))
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapCatalogueModule();
app.MapSalesModule();
app.MapAccountsModule();
app.MapConstructionModule();
app.MapPaymentsModule();
app.MapExtrasModule();
app.MapNotificationsModule();

app.Run();
