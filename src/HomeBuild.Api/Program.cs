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

builder.Services.AddCatalogueModule();
builder.Services.AddSalesModule();
var app = builder.Build();

app.MapCatalogueModule();
app.MapSalesModule();

app.Run();
