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

/// <summary>Marks an endpoint that works without a tenant (for example GET /me).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class TenantNotRequiredAttribute : Attribute;

/// <summary>
/// Sets the request's tenant. A signed-in user's tenant comes only from the <c>tenant_id</c> claim that
/// <c>TenantClaimsTransformation</c> derives from their membership; any X-Tenant-Id header is ignored, and a user with no
/// membership gets 403. Anonymous callers can only reach the public catalogue reads; for those the tenant (which
/// builder's catalogue) still comes from X-Tenant-Id.
/// </summary>
internal sealed class TenantMiddleware(RequestDelegate next)
{
    public const string Header = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, RequestTenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        if (context.Request.Path.StartsWithSegments("/openapi", StringComparison.Ordinal)
            || context.GetEndpoint()?.Metadata.GetMetadata<TenantNotRequiredAttribute>() is not null)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (!Guid.TryParse(context.User.FindFirst(Tenancy.TenantClaim)?.Value, out var claimed))
            {
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "no-membership", "No tenant membership",
                    "This user does not belong to any tenant in HomeBuild.");
                return;
            }

            tenant.TenantId = claimed;
            await next(context);
            return;
        }

        if (!Guid.TryParse(context.Request.Headers[Header], out var publicTenant) || publicTenant == Guid.Empty)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "tenant-required", "Tenant required",
                $"Anonymous catalogue reads name the builder in the {Header} header.");
            return;
        }

        tenant.TenantId = publicTenant;
        await next(context);
    }

    private static async Task WriteProblemAsync(HttpContext context, int status, string type, string title, string detail)
    {
        context.Response.StatusCode = status;
        var problem = Problems.Business(context, type, title, status, detail);
        await context.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}

/// <summary>Tenancy constants.</summary>
public static class Tenancy
{
    /// <summary>The claim HomeBuild adds from the membership table.</summary>
    public const string TenantClaim = "tenant_id";
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
