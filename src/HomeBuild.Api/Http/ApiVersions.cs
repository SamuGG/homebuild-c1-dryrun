using Asp.Versioning;
using Asp.Versioning.Builder;

namespace HomeBuild.Api.Http;

/// <summary>API versions: v1 (deprecated, sunset 31 May 2027) and v2. The version is the first URL segment.</summary>
internal static class ApiVersions
{
    public static readonly ApiVersion V1 = new(1);
    public static readonly ApiVersion V2 = new(2);
    public const string Prefix = "/v{version:apiVersion}";

    public static IServiceCollection AddHomeBuildApiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = V1;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
            options.ReportApiVersions = true;
            options.Policies.Deprecate(1).Effective(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero)).Link("https://homebuild.dev/api/versions#v1");
            options.Policies.Sunset(1).Effective(new DateTimeOffset(2027, 5, 31, 0, 0, 0, TimeSpan.Zero)).Link("https://homebuild.dev/api/versions#v1");
        })
        .AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'V";
            options.SubstituteApiVersionInUrl = true;
        });
        return services;
    }

    /// <summary>A module's versioned route group: every slice exists in v1 and v2 unless it maps itself to one.</summary>
    public static RouteGroupBuilder MapVersionedGroup(this IEndpointRouteBuilder endpoints)
    {
        ApiVersionSet versions = endpoints.NewApiVersionSet().HasApiVersion(V1).HasApiVersion(V2).ReportApiVersions().Build();
        return endpoints.MapGroup(Prefix).WithApiVersionSet(versions);
    }
}
