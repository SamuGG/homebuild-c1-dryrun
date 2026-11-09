using HomeBuild.Api;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole();
}

builder.Services.AddReservationPolicy();
var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
