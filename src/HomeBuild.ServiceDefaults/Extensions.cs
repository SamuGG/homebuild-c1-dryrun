using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// C11 Lab 4: from `aspire new aspire-starter` (Aspire CLI 17.0.0). Common Aspire services: service discovery,
// resilience, health checks, and OpenTelemetry. Referenced by the API, the web app and the workers.
// Changes from the template: /alive is mapped in every environment (the images' liveness check, Lab 1), the endpoints
// can be configured by the caller (the API marks them anonymous and tenant-free), and traces include Npgsql's
// database spans. See https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string ReadinessEndpointPath = "/ready";

    /// <summary>C11 Lab 6: the tag for readiness checks (dependencies: database, queues, the API). Liveness has none.</summary>
    public const string ReadyTag = "ready";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource("Npgsql") // Npgsql's ActivitySource: one span per database command
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        // C11 Lab 6: readiness runs on a schedule, not on every probe. The publisher runs the "ready" checks every
        // HealthChecks:Period (default 10 s) and the cache keeps the last report; /ready only reads it.
        builder.Services.AddSingleton<ReadinessCache>();
        builder.Services.AddSingleton<IHealthCheckPublisher>(sp => sp.GetRequiredService<ReadinessCache>());
        builder.Services.Configure<HealthCheckPublisherOptions>(options =>
        {
            options.Predicate = r => r.Tags.Contains(ReadyTag);
            options.Delay = builder.Configuration.GetValue("HealthChecks:Delay", TimeSpan.FromSeconds(1));
            options.Period = builder.Configuration.GetValue("HealthChecks:Period", TimeSpan.FromSeconds(10));
            options.Timeout = TimeSpan.FromSeconds(10); // the whole run; each check has its own, shorter timeout
        });

        return builder;
    }

    /// <summary>
    /// C11 Lab 6: a readiness check ("can this process do useful work now?") with a short timeout: a slow dependency
    /// reports Unhealthy after <paramref name="timeout"/> (default 2 s) instead of holding the report.
    /// </summary>
    public static IHealthChecksBuilder AddReadinessCheck<T>(this IHealthChecksBuilder builder, string name, TimeSpan? timeout = null)
        where T : class, IHealthCheck
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<T>(name, HealthStatus.Unhealthy, [ReadyTag], timeout ?? TimeSpan.FromSeconds(2));
    }

    /// <summary>A readiness check from a delegate (for checks that need constructor arguments, such as a queue URL).</summary>
    public static IHealthChecksBuilder AddReadinessCheck(this IHealthChecksBuilder builder, string name, Func<IServiceProvider, IHealthCheck> factory, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<HealthCheckServiceOptions>(options => options.Registrations.Add(
            new HealthCheckRegistration(name, factory, HealthStatus.Unhealthy, [ReadyTag], timeout ?? TimeSpan.FromSeconds(2))));
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app, Action<IEndpointConventionBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Liveness ("does the process answer?") only runs checks tagged "live": safe to expose, and the container
        // images (Lab 1) need it in every environment.
        var alive = app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });
        configure?.Invoke(alive);

        // C11 Lab 6: readiness from the cached report (503 until the first run, and while any "ready" check fails). Mapped
        // in every environment; C12 keeps it off the public internet (the load balancer's target group only).
        var ready = app.MapGet(ReadinessEndpointPath, (ReadinessCache cache) => cache.Latest is not { } report
            ? Results.Json(new { status = "Starting" }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(new
            {
                status = report.Status.ToString(),
                checkedAt = cache.CheckedAt,
                entries = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), e.Value.Description, durationMs = (int)e.Value.Duration.TotalMilliseconds }),
            }, statusCode: report.Status == HealthStatus.Healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
        configure?.Invoke(ready);

        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            var health = app.MapHealthChecks(HealthEndpointPath);
            configure?.Invoke(health);
        }

        return app;
    }
}

/// <summary>C11 Lab 6: the last readiness report, published on a schedule (IHealthCheckPublisher); /ready reads it.</summary>
public sealed class ReadinessCache(TimeProvider? time = null) : IHealthCheckPublisher
{
    public HealthReport? Latest { get; private set; }

    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>Raised after each report, so other parts of the host (queue consumers) can follow readiness.</summary>
    public event Action<HealthReport>? Published;

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        Latest = report;
        CheckedAt = (time ?? TimeProvider.System).GetUtcNow();
        Published?.Invoke(report);
        return Task.CompletedTask;
    }
}
