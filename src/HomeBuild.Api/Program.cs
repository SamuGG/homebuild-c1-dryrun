using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Sales;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole();
}

builder.Services.AddCatalogueModule();
builder.Services.AddSalesModule();
var app = builder.Build();

app.MapCatalogueModule();
app.MapSalesModule();

app.Run();
