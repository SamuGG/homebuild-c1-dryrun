using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace HomeBuild.Web.Auth;

/// <summary>A signed-in session's tokens. They never leave the server.</summary>
public sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Server-side token storage, keyed by the session ID in the user's cookie. The browser's cookie holds only
/// that ID (and the user's claims), encrypted and HttpOnly; the tokens stay here.
/// </summary>
public sealed class TokenStore(IDistributedCache cache)
{
    /// <summary>Matches the Cognito refresh token's lifetime (1 day): after that the session cannot be refreshed.</summary>
    private static readonly DistributedCacheEntryOptions Lifetime = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) };

    public const string SessionClaim = "homebuild_session";

    public async Task<TokenSet?> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
        await cache.GetAsync(Key(sessionId), cancellationToken) is { } bytes ? JsonSerializer.Deserialize<TokenSet>(bytes) : null;

    public Task SetAsync(string sessionId, TokenSet tokens, CancellationToken cancellationToken = default) =>
        cache.SetAsync(Key(sessionId), JsonSerializer.SerializeToUtf8Bytes(tokens), Lifetime, cancellationToken);

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(Key(sessionId), cancellationToken);

    private static string Key(string sessionId) => "tokens:" + sessionId;
}
