using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace HomeBuild.Web.Auth;

/// <summary>
/// Attaches the signed-in user's access token to API calls made by the web server. The user comes from the circuit
/// (interactive components) or the current request (static SSR); the token from <see cref="UserTokenService"/>.
/// </summary>
public sealed class AccessTokenHandler(
    UserTokenService tokens,
    CircuitServicesAccessor circuit,
    IHttpContextAccessor httpContext) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync();
        if (user is not null && await tokens.GetAccessTokenAsync(user, cancellationToken) is { } accessToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<ClaimsPrincipal?> CurrentUserAsync()
    {
        if (circuit.Services?.GetService<AuthenticationStateProvider>() is { } state)
        {
            return (await state.GetAuthenticationStateAsync()).User;
        }

        // Static SSR: a DelegatingHandler is not a component, so the request's user is the right source here.
        return httpContext.HttpContext?.User;
    }
}
