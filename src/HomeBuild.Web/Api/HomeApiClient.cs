using System.Net.Http.Json;

namespace HomeBuild.Web.Api;

public sealed record CurrentUser(string Subject, string ClientId, IReadOnlyList<string> Roles, Guid? TenantId);

public sealed record Reservation(Guid Id, Guid UnitId, Guid BuyerId, decimal Deposit, string Currency, string State, DateTimeOffset ExpiresAt);

/// <summary>Typed client for the API's signed-in endpoints. <c>AccessTokenHandler</c> adds the user's token.</summary>
public sealed class HomeApiClient(HttpClient http)
{
    public async Task<CurrentUser> GetMeAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<CurrentUser>("me", cancellationToken) ?? throw new InvalidOperationException("The API returned no user.");

    /// <summary>Reserves a unit for the signed-in buyer: the new reservation's ID, or the API's problem (400, 409, 422).</summary>
    public async Task<ApiResult<Guid>> ReserveAsync(Guid unitId, decimal deposit, string currency, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"units/{unitId}/reservations", new { deposit, currency }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new(default, await ApiProblem.ReadAsync(response, cancellationToken));
        }

        var created = await response.Content.ReadFromJsonAsync<Reservation>(cancellationToken);
        return new(created!.Id, null);
    }

    /// <summary>A reservation the signed-in user may read, or null (the API answers 404 for other buyers' reservations).</summary>
    public async Task<Reservation?> GetReservationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(new Uri($"reservations/{id}", UriKind.Relative), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Reservation>(cancellationToken);
    }

    /// <summary>Edits a unit's price and description (admin).</summary>
    public async Task<ApiResult<bool>> EditUnitAsync(Guid unitId, decimal price, string description, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"units/{unitId}", new { price, description }, cancellationToken);
        return response.IsSuccessStatusCode ? new(true, null) : new(false, await ApiProblem.ReadAsync(response, cancellationToken));
    }
}
