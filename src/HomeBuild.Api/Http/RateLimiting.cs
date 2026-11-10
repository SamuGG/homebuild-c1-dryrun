using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace HomeBuild.Api.Http;

/// <summary>Rate limits for write endpoints. Partitioned per client IP until C17 moves to tenant.</summary>
internal static class RateLimiting
{
    public const string Reservations = "reservations";
    public const int PermitLimit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddHomeBuildRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // The default is 503, which tells clients the server is broken rather than that they are too fast.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(Reservations, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = Window, QueueLimit = 0 }));
            options.OnRejected = WriteProblemAsync;
        });
        return services;
    }

    private static async ValueTask WriteProblemAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problem = Problems.Business(
            http, "too-many-requests", "Too many requests", StatusCodes.Status429TooManyRequests,
            $"At most {PermitLimit} reservation requests per {Window.TotalMinutes:0} minute from one client. Retry after the Retry-After interval.");
        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }
}
