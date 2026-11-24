using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace HomeBuild.Api.Tests;

/// <summary>C11 Lab 6: liveness has no dependencies; readiness checks them on a schedule, each with a short timeout.</summary>
public sealed class ReadinessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A database outage makes the API not ready (no traffic) but leaves it alive (no restart): restarting cannot fix
    // the database, and every instance restarting in a loop makes the outage worse.
    [Fact]
    public async Task Ready_is_unhealthy_when_database_down_but_alive_is_healthy()
    {
        await using var api = new ApiFactory("Host=127.0.0.1;Port=1;Database=homebuild;Username=homebuild;Password=unused;Timeout=1");
        await using var factory = api.WithWebHostBuilder(b => b
            .UseSetting("HealthChecks:Delay", "00:00:00")
            .UseSetting("HealthChecks:Period", "00:00:01")); // the publisher refuses less than one second
        using var client = factory.CreateClient();
        // Probes are anonymous (a load balancer or orchestrator). A signed-in caller would still touch the database in
        // the tenant claims transformation (C4), before any endpoint runs.
        client.DefaultRequestHeaders.Authorization = null;

        using var alive = await client.GetAsync(new Uri("/alive", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);

        JsonElement ready = default;
        var status = HttpStatusCode.OK;
        for (var i = 0; i < 50; i++)
        {
            using var response = await client.GetAsync(new Uri("/ready", UriKind.Relative), Ct);
            status = response.StatusCode;
            ready = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
            if (ready.GetProperty("status").GetString() != "Starting")
            {
                break;
            }

            await Task.Delay(100, Ct);
        }

        TestContext.Current.TestOutputHelper?.WriteLine(ready.ToString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", ready.GetProperty("status").GetString());
        var database = ready.GetProperty("entries").GetProperty("database");
        Assert.Equal("Unhealthy", database.GetProperty("status").GetString());
        // EF's retry strategy keeps retrying a dead port, so the check usually ends at its own 2 s timeout.
        Assert.True(database.GetProperty("durationMs").GetDouble() < 3000, ready.ToString());
    }

    // A dependency that hangs must not hold readiness: the check reports Unhealthy at its own timeout.
    [Fact]
    public async Task Health_check_with_slow_dependency_times_out_quickly()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        builder.Services.AddHealthChecks().AddReadinessCheck<HangingCheck>("slow-dependency", TimeSpan.FromMilliseconds(500));
        using var host = builder.Build();
        var health = host.Services.GetRequiredService<HealthCheckService>();

        var clock = Stopwatch.StartNew();
        var report = await health.CheckHealthAsync(r => r.Tags.Contains(Extensions.ReadyTag), Ct);
        clock.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"slow-dependency: {report.Entries["slow-dependency"].Status} after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["slow-dependency"].Status);
        Assert.InRange(clock.ElapsedMilliseconds, 400, 2_000);
    }

    private sealed class HangingCheck : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken); // a dependency that never answers
            return HealthCheckResult.Healthy();
        }
    }
}
