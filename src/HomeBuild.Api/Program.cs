using HomeBuild.Api.Http;
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

builder.Services.AddValidation();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context => Problems.AddTraceId(context.ProblemDetails, context.HttpContext));
builder.Services.AddExceptionHandler<LoggingExceptionHandler>();

builder.Services.AddCatalogueModule();
builder.Services.AddSalesModule();
var app = builder.Build();

// Pipeline order, one reason per line:
// 1. Exception handler: first, so it wraps everything after it and turns any exception into a 500 ProblemDetails.
app.UseExceptionHandler();
// 2. Status code pages: gives empty error responses (404, 405, filter results) a ProblemDetails body; it must sit inside the exception handler.
app.UseStatusCodePages();
// 3. Routing: picks the endpoint, so middleware after it (rate limiter in Lab 7, auth in C4) can read endpoint metadata.
app.UseRouting();

app.MapCatalogueModule();
app.MapSalesModule();

app.Run();
