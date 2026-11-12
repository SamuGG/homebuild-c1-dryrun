using System.Net.Http.Json;

namespace HomeBuild.Web.Api;

public sealed record CurrentUser(string Subject, string ClientId, IReadOnlyList<string> Roles, Guid? TenantId);

public sealed record Board(Guid DevelopmentId, string DevelopmentName, IReadOnlyList<string> Milestones, IReadOnlyList<BoardRow> Rows);

public sealed record BoardRow(Guid UnitId, string PlotNumber, IReadOnlyList<BoardCell> Cells)
{
    /// <summary>The first milestone not yet done, or null when the unit is finished.</summary>
    public string? CurrentStage => Cells.FirstOrDefault(c => c.Status != "Done")?.Milestone;
}

public sealed record BoardCell(string Milestone, string Status, DateOnly? CompletedOn, string Subcontractor, string SiteNotes, int OpenDefects);

public sealed record BuyerProgress(string Stage, int StepsDone, int StepsTotal, string? NextStep);

public sealed record BuyerHome(Guid ReservationId, Guid UnitId, string PlotNumber, string DevelopmentName, string ReservationState, DateTimeOffset ReservationExpiresAt, BuyerProgress Progress);

public sealed record MyProgress(IReadOnlyList<BuyerHome> Homes);

public sealed record Reservation(Guid Id, Guid UnitId, Guid BuyerId, decimal Deposit, string Currency, string State, DateTimeOffset ExpiresAt);

/// <summary>Typed client for the API's signed-in endpoints. <c>AccessTokenHandler</c> adds the user's token.</summary>
public sealed class HomeApiClient(HttpClient http)
{
    public async Task<CurrentUser> GetMeAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<CurrentUser>("me", cancellationToken) ?? throw new InvalidOperationException("The API returned no user.");

    /// <summary>The staff build board (site managers and admins), or null if the development is unknown.</summary>
    public async Task<Board?> GetBoardAsync(Guid developmentId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(new Uri($"developments/{developmentId}/board", UriKind.Relative), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Board>(cancellationToken);
    }

    /// <summary>The signed-in buyer's homes and simplified progress (the buyer DTO; no internal fields).</summary>
    public async Task<MyProgress> GetMyProgressAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<MyProgress>("me/progress", cancellationToken) ?? new MyProgress([]);

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

    /// <summary>The API's pre-signed POST for a floor plan, passed through unchanged (the browser posts it to S3).</summary>
    public async Task<ApiResult<System.Text.Json.JsonElement>> StartFloorPlanUploadAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(new Uri($"units/{unitId}/floor-plan/uploads", UriKind.Relative), null, cancellationToken);
        return response.IsSuccessStatusCode
            ? new(await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken), null)
            : new(default, await ApiProblem.ReadAsync(response, cancellationToken));
    }

    /// <summary>Asks the API to check an uploaded floor plan and move it into place: its final key, or the problem.</summary>
    public async Task<ApiResult<string>> CompleteFloorPlanUploadAsync(Guid unitId, string key, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"units/{unitId}/floor-plan", new { key }, cancellationToken);
        return response.IsSuccessStatusCode
            ? new((await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken)).GetProperty("key").GetString(), null)
            : new(null, await ApiProblem.ReadAsync(response, cancellationToken));
    }

    /// <summary>Edits a unit's price and description (admin).</summary>
    public async Task<ApiResult<bool>> EditUnitAsync(Guid unitId, decimal price, string description, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"units/{unitId}", new { price, description }, cancellationToken);
        return response.IsSuccessStatusCode ? new(true, null) : new(false, await ApiProblem.ReadAsync(response, cancellationToken));
    }
}
