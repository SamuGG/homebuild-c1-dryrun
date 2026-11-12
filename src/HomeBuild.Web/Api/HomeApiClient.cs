using System.Net.Http.Json;

namespace HomeBuild.Web.Api;

public sealed record CurrentUser(string Subject, string ClientId, IReadOnlyList<string> Roles, Guid? TenantId);

/// <summary>Typed client for the API's signed-in endpoints. <c>AccessTokenHandler</c> adds the user's token.</summary>
public sealed class HomeApiClient(HttpClient http)
{
    public async Task<CurrentUser> GetMeAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<CurrentUser>("me", cancellationToken) ?? throw new InvalidOperationException("The API returned no user.");
}
