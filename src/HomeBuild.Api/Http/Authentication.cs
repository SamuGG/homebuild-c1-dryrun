using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace HomeBuild.Api.Http;

/// <summary>Which access tokens HomeBuild accepts besides the issuer and signature checks.</summary>
public sealed class TokenOptions
{
    public const string Section = "Tokens";

    /// <summary>
    /// App clients allowed to call the API. Cognito access tokens carry <c>client_id</c> instead of <c>aud</c>,
    /// so this list replaces audience validation.
    /// </summary>
    public IReadOnlyList<string> AllowedClientIds { get; set; } = [];
}

internal static class Authentication
{
    public const string GroupsClaim = "cognito:groups";

    /// <summary>
    /// JWT bearer for Cognito access tokens. Issuer, signing keys and lifetime come from the standard
    /// Authentication:Schemes:Bearer configuration: <c>Authority</c> (the user pool's issuer, keys from its JWKS) in
    /// AWS, or <c>ValidIssuer</c> + <c>SigningKeys</c> from a local test issuer (dotnet user-jwts, tests, CI).
    /// </summary>
    public static IServiceCollection AddHomeBuildAuthentication(this IServiceCollection services)
    {
        services.AddOptions<TokenOptions>().BindConfiguration(TokenOptions.Section);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false; // keep Cognito's claim names (sub, cognito:groups, client_id)
                options.TokenValidationParameters.ValidateAudience = false; // replaced by the client_id check below
                options.TokenValidationParameters.RoleClaimType = GroupsClaim;
                options.TokenValidationParameters.NameClaimType = "sub";
                options.Events = new JwtBearerEvents { OnTokenValidated = CheckAccessTokenAsync };
            });
        services.AddAuthorizationBuilder()
            // Every endpoint needs a signed-in user unless it opts out with AllowAnonymous (public catalogue reads).
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }

    private static Task CheckAccessTokenAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;
        var allowed = context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<TokenOptions>>().Value;
        if (principal.FindFirstValue("token_use") != "access")
        {
            context.Fail("Only access tokens are accepted (token_use must be 'access').");
        }
        else if (principal.FindFirstValue("client_id") is not { } clientId || !allowed.AllowedClientIds.Contains(clientId, StringComparer.Ordinal))
        {
            context.Fail("The token was issued to a client that may not call this API.");
        }

        return Task.CompletedTask;
    }
}
