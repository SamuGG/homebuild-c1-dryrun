using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Accounts;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Sales;

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddCatalogueModule();
builder.Services.AddSalesModule();
builder.Services.AddAccountsModule();
var app = builder.Build();

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

// The OpenAPI document is published only in Development and in CI, where Bruno and reviewers use it.
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("CI"))
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapCatalogueModule();
app.MapSalesModule();
app.MapAccountsModule();

app.Run();
