using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HomeBuild.Web.Auth;

/// <summary>Sign-in through a backend-for-frontend: Cognito OIDC (code + PKCE) on the server, a cookie in the browser.</summary>
public static class SignIn
{
    public const string CookieName = "homebuild.session";

    public static IServiceCollection AddHomeBuildSignIn(this IServiceCollection services)
    {
        services.AddOptions<CognitoOptions>().BindConfiguration(CognitoOptions.Section);
        services.AddDistributedMemoryCache();
        services.AddSingleton<TokenStore>();
        services.AddSingleton<UserTokenService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(UserTokenService.TokenClient);
        services.AddHttpContextAccessor();
        services.AddSingleton<CircuitServicesAccessor>();
        services.AddScoped<CircuitHandler, ServicesAccessorCircuitHandler>();
        services.AddTransient<AccessTokenHandler>();
        services.AddCascadingAuthenticationState();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;                           // no script can read it
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;  // HTTPS only (browsers treat localhost as secure)
                options.Cookie.SameSite = SameSiteMode.Lax;               // not sent on cross-site POSTs
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.Events.OnValidatePrincipal = EndSessionWithoutTokensAsync;
            })
            .AddOpenIdConnect();

        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<CognitoOptions>>((oidc, cognito) =>
            {
                oidc.Authority = cognito.Value.Authority;
                oidc.ClientId = cognito.Value.ClientId;
                oidc.ClientSecret = cognito.Value.ClientSecret;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.Scope.Add("email");
                oidc.Scope.Add("profile");
                oidc.MapInboundClaims = false; // keep Cognito's names: sub, email, cognito:groups
                oidc.TokenValidationParameters.NameClaimType = "email";
                oidc.TokenValidationParameters.RoleClaimType = "cognito:groups";
                oidc.SaveTokens = false; // tokens go to the server-side TokenStore, never into the cookie
                oidc.Events.OnTokenValidated = KeepTokensOnServerAsync;
                oidc.Events.OnRedirectToIdentityProviderForSignOut = RedirectToCognitoLogoutAsync;
            });
        return services;
    }

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/account");
        account.MapGet("/login", (string? returnUrl) => TypedResults.Challenge(
            new AuthenticationProperties { RedirectUri = LocalOnly(returnUrl) },
            [OpenIdConnectDefaults.AuthenticationScheme])).AllowAnonymous();

        // POST with an antiforgery token: a GET logout could be triggered by any page.
        account.MapPost("/logout", async (HttpContext context, TokenStore store, [FromForm] string? returnUrl) =>
        {
            if (context.User.FindFirstValue(TokenStore.SessionClaim) is { } sessionId)
            {
                await store.RemoveAsync(sessionId, context.RequestAborted);
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.SignOut(new AuthenticationProperties { RedirectUri = LocalOnly(returnUrl) },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        });
        return endpoints;
    }

    private static string LocalOnly(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";

    // The code exchange is done; move the tokens into the server-side store and give the session an ID.
    private static async Task KeepTokensOnServerAsync(TokenValidatedContext context)
    {
        var response = context.TokenEndpointResponse!;
        var services = context.HttpContext.RequestServices;
        var tokens = services.GetRequiredService<UserTokenService>().ToTokenSet(response.AccessToken, response.RefreshToken, response.ExpiresIn);
        var sessionId = Guid.NewGuid().ToString("N");
        await services.GetRequiredService<TokenStore>().SetAsync(sessionId, tokens, context.HttpContext.RequestAborted);
        ((ClaimsIdentity)context.Principal!.Identity!).AddClaim(new Claim(TokenStore.SessionClaim, sessionId));
    }

    // A cookie whose tokens are gone (expired store entry, failed refresh, restarted server) is not a session.
    private static async Task EndSessionWithoutTokensAsync(CookieValidatePrincipalContext context)
    {
        var store = context.HttpContext.RequestServices.GetRequiredService<TokenStore>();
        if (context.Principal?.FindFirstValue(TokenStore.SessionClaim) is not { } sessionId
            || await store.GetAsync(sessionId, context.HttpContext.RequestAborted) is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    // Cognito's discovery document has no end_session_endpoint; its logout endpoint is /logout on the managed
    // login domain, with client_id and a logout_uri registered on the app client. It ends the Cognito session too,
    // so the next sign-in asks for the password again (a shared computer).
    private static async Task RedirectToCognitoLogoutAsync(RedirectContext context)
    {
        var configuration = await context.Options.ConfigurationManager!.GetConfigurationAsync(context.HttpContext.RequestAborted);
        var domain = new Uri(configuration.AuthorizationEndpoint).GetLeftPart(UriPartial.Authority);
        var request = context.Request;
        var logoutUri = $"{request.Scheme}://{request.Host}/";
        context.Response.Redirect($"{domain}/logout?client_id={Uri.EscapeDataString(context.Options.ClientId!)}&logout_uri={Uri.EscapeDataString(logoutUri)}");
        context.HandleResponse();
    }
}
