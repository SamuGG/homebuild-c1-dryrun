namespace HomeBuild.Api.Http;

/// <summary>The tenant of the current request. C4 replaces the header with a token claim.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}

/// <summary>Scoped: one per request, set by <see cref="TenantMiddleware"/>.</summary>
internal sealed class RequestTenantContext : ITenantContext
{
    public Guid? TenantId { get; set; }
}

/// <summary>A fixed tenant (or none) for tools, tests and migrations.</summary>
public sealed class FixedTenantContext(Guid? tenantId) : ITenantContext
{
    public static FixedTenantContext None { get; } = new(null);

    public Guid? TenantId { get; } = tenantId;
}

/// <summary>Reads X-Tenant-Id into the scoped tenant context; API calls without a valid one get 400.</summary>
internal sealed class TenantMiddleware(RequestDelegate next)
{
    public const string Header = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, RequestTenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        if (context.Request.Path.StartsWithSegments("/openapi", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (!Guid.TryParse(context.Request.Headers[Header], out var tenantId) || tenantId == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            var problem = Problems.Business(context, "tenant-required", "Tenant required", StatusCodes.Status400BadRequest,
                $"Send the tenant in the {Header} header.");
            await context.RequestServices.GetRequiredService<IProblemDetailsService>()
                .WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
            return;
        }

        tenant.TenantId = tenantId;
        await next(context);
    }
}

public static class TenancyServiceCollectionExtensions
{
    public static IServiceCollection AddTenancy(this IServiceCollection services)
    {
        // Scoped: the tenant belongs to one request.
        services.AddScoped<RequestTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<RequestTenantContext>());
        return services;
    }
}
