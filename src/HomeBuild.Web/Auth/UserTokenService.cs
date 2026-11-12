using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace HomeBuild.Web.Auth;

/// <summary>
/// Gives server code the signed-in user's access token, refreshing it with the refresh token when it has expired
/// (or will within a minute). Concurrent callers for one session share a single refresh.
/// </summary>
public sealed partial class UserTokenService(
    TokenStore store,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<CognitoOptions> cognito,
    TimeProvider time,
    ILogger<UserTokenService> logger)
{
    public const string TokenClient = "cognito-token";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> refreshLocks = new();

    /// <summary>The access token, or null when the user is not signed in or the session can no longer be refreshed.</summary>
    public async Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (user.FindFirstValue(TokenStore.SessionClaim) is not { } sessionId)
        {
            return null;
        }

        var tokens = await store.GetAsync(sessionId, cancellationToken);
        if (tokens is null || IsFresh(tokens))
        {
            return tokens?.AccessToken;
        }

        var gate = refreshLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while this one waited.
            tokens = await store.GetAsync(sessionId, cancellationToken);
            if (tokens is null || IsFresh(tokens))
            {
                return tokens?.AccessToken;
            }

            var refreshed = await RefreshAsync(tokens, cancellationToken);
            if (refreshed is null)
            {
                await store.RemoveAsync(sessionId, cancellationToken);
                return null;
            }

            await store.SetAsync(sessionId, refreshed, cancellationToken);
            return refreshed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Builds a token set from a token endpoint response (sign-in or refresh).</summary>
    public TokenSet ToTokenSet(string accessToken, string? refreshToken, string? expiresIn, string fallbackRefreshToken = "") =>
        new(accessToken, refreshToken ?? fallbackRefreshToken,
            time.GetUtcNow().AddSeconds(int.TryParse(expiresIn, out var seconds) ? seconds : 0));

    private bool IsFresh(TokenSet tokens) => tokens.ExpiresAt - RefreshMargin > time.GetUtcNow();

    private async Task<TokenSet?> RefreshAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = cognito.Value.ClientId,
                ["refresh_token"] = tokens.RefreshToken,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(cognito.Value.ClientId)}:{Uri.EscapeDataString(cognito.Value.ClientSecret)}")));

        using var response = await httpClientFactory.CreateClient(TokenClient).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogRefreshFailed(logger, (int)response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<RefreshResponse>(cancellationToken);
        LogRefreshed(logger);
        // Cognito does not rotate refresh tokens unless rotation is enabled, so the old one stays valid.
        return body is null ? null : ToTokenSet(body.AccessToken, body.RefreshToken, body.ExpiresIn.ToString(System.Globalization.CultureInfo.InvariantCulture), tokens.RefreshToken);
    }

    private sealed record RefreshResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    [LoggerMessage(Level = LogLevel.Information, Message = "Access token refreshed")]
    private static partial void LogRefreshed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access token refresh failed with status {Status}; the session ends")]
    private static partial void LogRefreshFailed(ILogger logger, int status);
}
