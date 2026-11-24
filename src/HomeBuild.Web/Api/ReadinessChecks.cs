using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using HomeBuild.Web.Catalogue;
using Microsoft.Extensions.Options;

namespace HomeBuild.Web.Api;

/// <summary>C11 Lab 6, readiness: the API answers its liveness endpoint (the web app can do nothing useful without it).</summary>
internal sealed class ApiReachableCheck(IHttpClientFactory httpClientFactory, IOptions<CatalogueApiOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var alive = new Uri(new Uri(options.Value.BaseAddress!.GetLeftPart(UriPartial.Authority)), "/alive");
        try
        {
            using var response = await httpClientFactory.CreateClient(nameof(ApiReachableCheck)).GetAsync(alive, cancellationToken);
            return response.IsSuccessStatusCode ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy($"API /alive answered {(int)response.StatusCode}");
        }
#pragma warning disable CA1031 // any failure means "not ready"
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy($"API unreachable: {e.GetType().Name}");
        }
    }
}

/// <summary>C11 Lab 6, readiness: the token store (Valkey under the AppHost) answers; without it nobody stays signed in.</summary>
internal sealed class TokenCacheReachableCheck(IDistributedCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await cache.GetAsync("health:probe", cancellationToken);
            return HealthCheckResult.Healthy();
        }
#pragma warning disable CA1031 // any failure means "not ready"
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy($"Token store unreachable: {e.GetType().Name}");
        }
    }
}
