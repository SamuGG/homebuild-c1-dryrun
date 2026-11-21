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

/// <summary>C10 Lab 1: an extra and its window (unlock and close milestones).</summary>
public sealed record Extra(Guid Id, string Name, string Category, decimal Price, string Currency, string UnlocksAt, string ClosesAt, string? ConflictGroup, int MaxQuantity);

/// <summary>C10 Lab 1: the extras open now for one of the buyer's homes, from Extras' own copy of its progress.</summary>
public sealed record MyExtras(Guid UnitId, string? LatestMilestone, IReadOnlyList<Extra> OpenExtras);

/// <summary>C10 Lab 2: a basket line with its snapshot price; Status: in-basket, paid, refund-pending, refunded.</summary>
public sealed record BasketLine(Guid Id, Guid ExtraId, string Name, decimal UnitPrice, string Currency, int Quantity, string Status, string? Reason, string State = "");

/// <summary>C10 Lab 3: the staff DTO of a change order (site managers see who acknowledged it; buyers never do).</summary>
public sealed record ChangeOrder(Guid Id, Guid UnitId, string ExtraName, int Quantity, string FitBy, string Status, DateTimeOffset CreatedAt, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, DateTimeOffset? FittedAt);

/// <summary>C10 Lab 2: Status open, confirming (Stripe has not confirmed the payment yet) or paid.</summary>
public sealed record Basket(Guid Id, Guid UnitId, string Status, IReadOnlyList<BasketLine> Lines, decimal Total, string Currency, DateTimeOffset? PaidAt);

public sealed record BasketCheckout(string SessionId, Uri Url, DateTimeOffset ExpiresAt);

/// <summary>C10 Lab 4: the handover pack in My Home.</summary>
public sealed record HandoverPack(Guid UnitId, bool KeysHandedOver, string? MeterReadings, bool WarrantiesIssued, DateTimeOffset? CompletedAt, DateOnly? DefectsEndOn, IReadOnlyList<string> Documents);

public sealed record SnagEntry(string Kind, string Actor, DateTimeOffset At, string Text, string? From, string? To, bool Internal = false);

/// <summary>C10 Lab 4: a snag (the buyer's DTO has no internal notes; Aftercare's has them, flagged Internal).</summary>
public sealed record Snag(Guid Id, Guid UnitId, string Area, string Description, string Severity, string State, string? Subcontractor, DateOnly? VisitOn, IReadOnlyList<SnagEntry> Timeline);

public sealed record DepositSession(string SessionId, Uri Url, DateTimeOffset ExpiresAt);

/// <summary>C8 Lab 5: a deposit and what happened to it (paid, refund-pending, refunded), with the reason.</summary>
public sealed record MyDeposit(Guid ReservationId, decimal Amount, string Currency, DateTimeOffset PaidAt, string Status, string? Reason, DateTimeOffset? RefundedAt);

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

    /// <summary>C10 Lab 1: the extras the signed-in buyer can choose now, per home.</summary>
    public async Task<IReadOnlyList<MyExtras>> GetMyExtrasAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<MyExtras>>("me/extras", cancellationToken) ?? [];

    /// <summary>C10 Lab 1: every extra of a development with its window (staff).</summary>
    public async Task<IReadOnlyList<Extra>> GetDevelopmentExtrasAsync(Guid developmentId, CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<Extra>>($"developments/{developmentId}/extras", cancellationToken) ?? [];

    /// <summary>C10 Lab 4: the buyer's handover packs.</summary>
    public async Task<IReadOnlyList<HandoverPack>> GetMyHandoverAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<HandoverPack>>("me/handover", cancellationToken) ?? [];

    /// <summary>C10 Lab 4: the buyer's snags (buyer DTO).</summary>
    public async Task<IReadOnlyList<Snag>> GetMySnagsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<Snag>>("me/snags", cancellationToken) ?? [];

    /// <summary>C10 Lab 4: every snag (Aftercare's DTO).</summary>
    public async Task<IReadOnlyList<Snag>> GetSnagsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<Snag>>("snags", cancellationToken) ?? [];

    public async Task<ApiResult<bool>> DraftSnagAsync(Guid unitId, string area, string description, string severity, CancellationToken cancellationToken = default) =>
        await PostAsync("me/snags", new { unitId, area, description, severity }, cancellationToken);

    /// <summary>C10 Lab 4: report, acknowledge, schedule, fix, confirm, dispute or close; the API refuses what the lifecycle does not allow (409).</summary>
    public async Task<ApiResult<bool>> ActOnSnagAsync(Guid id, string action, string? subcontractor = null, DateOnly? visitOn = null, string? reason = null, CancellationToken cancellationToken = default) =>
        await PostAsync($"snags/{id}/{action}", new { subcontractor, visitOn, reason }, cancellationToken);

    public async Task<ApiResult<bool>> NoteOnSnagAsync(Guid id, string text, CancellationToken cancellationToken = default) =>
        await PostAsync($"snags/{id}/notes", new { text }, cancellationToken);

    public async Task<ApiResult<bool>> RecordHandoverAsync(Guid unitId, bool keys, string? meterReadings, bool warranties, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"units/{unitId}/handover", new { keysHandedOver = keys, meterReadings, warrantiesIssued = warranties }, cancellationToken);
        return response.IsSuccessStatusCode ? new(true, null) : new(false, await ApiProblem.ReadAsync(response, cancellationToken));
    }

    private async Task<ApiResult<bool>> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, body, cancellationToken);
        return response.IsSuccessStatusCode ? new(true, null) : new(false, await ApiProblem.ReadAsync(response, cancellationToken));
    }

    /// <summary>C10 Lab 3: a development's change orders (staff board).</summary>
    public async Task<IReadOnlyList<ChangeOrder>> GetChangeOrdersAsync(Guid developmentId, CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<ChangeOrder>>($"developments/{developmentId}/change-orders", cancellationToken) ?? [];

    /// <summary>C10 Lab 3: acknowledge ("acknowledgement") or record as fitted ("fitting"); the API's problem on 403/409.</summary>
    public async Task<ApiResult<bool>> MoveChangeOrderAsync(Guid id, string step, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(new Uri($"change-orders/{id}/{step}", UriKind.Relative), null, cancellationToken);
        return response.IsSuccessStatusCode ? new(true, null) : new(false, await ApiProblem.ReadAsync(response, cancellationToken));
    }

    /// <summary>C10 Lab 2: the buyer's baskets and orders.</summary>
    public async Task<IReadOnlyList<Basket>> GetMyBasketsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<Basket>>("me/baskets", cancellationToken) ?? [];

    /// <summary>C10 Lab 2: adds an open extra to the basket for a home, or the API's problem (409 conflict, 422 closed or over the limit).</summary>
    public async Task<ApiResult<bool>> AddExtraAsync(Guid unitId, Guid extraId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("me/basket/lines", new { unitId, extraId, quantity = 1 }, cancellationToken);
        return response.IsSuccessStatusCode ? new(true, null) : new(false, await ApiProblem.ReadAsync(response, cancellationToken));
    }

    /// <summary>C10 Lab 2: revalidates the basket and opens a Stripe Checkout page for it: the URL, or the API's problem.</summary>
    public async Task<ApiResult<Uri>> StartBasketCheckoutAsync(Guid unitId, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var response = await PostIdempotentAsync("me/basket/checkout", JsonContent.Create(new { unitId }), idempotencyKey, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new(null, await ApiProblem.ReadAsync(response, cancellationToken));
        }

        var session = await response.Content.ReadFromJsonAsync<BasketCheckout>(cancellationToken);
        return new(session!.Url, null);
    }

    /// <summary>The signed-in buyer's homes and simplified progress (the buyer DTO; no internal fields).</summary>
    public async Task<MyProgress> GetMyProgressAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<MyProgress>("me/progress", cancellationToken) ?? new MyProgress([]);

    /// <summary>
    /// Reserves a unit for the signed-in buyer: the new reservation's ID, or the API's problem (400, 409, 422).
    /// <paramref name="idempotencyKey"/> comes from the form (C8 Lab 3): a double click or a retry sends the same key.
    /// </summary>
    public async Task<ApiResult<Guid>> ReserveAsync(Guid unitId, decimal deposit, string currency, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var response = await PostIdempotentAsync($"units/{unitId}/reservations", JsonContent.Create(new { deposit, currency }), idempotencyKey, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new(default, await ApiProblem.ReadAsync(response, cancellationToken));
        }

        var created = await response.Content.ReadFromJsonAsync<Reservation>(cancellationToken);
        return new(created!.Id, null);
    }

    /// <summary>C8 Lab 5: the signed-in buyer's deposits, so My Home can say why one was refunded.</summary>
    public async Task<IReadOnlyList<MyDeposit>> GetMyDepositsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<MyDeposit>>("me/deposits", cancellationToken) ?? [];

    /// <summary>
    /// C8 Lab 1: a Stripe Checkout page for a held reservation's deposit. The URL to send the buyer to, or the API's
    /// problem (404 not theirs, 409 not held or too late). The page's URL is never logged.
    /// </summary>
    public async Task<ApiResult<Uri>> StartDepositSessionAsync(Guid reservationId, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var response = await PostIdempotentAsync($"reservations/{reservationId}/deposit-session", null, idempotencyKey, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new(null, await ApiProblem.ReadAsync(response, cancellationToken));
        }

        var session = await response.Content.ReadFromJsonAsync<DepositSession>(cancellationToken);
        return new(session!.Url, null);
    }

    /// <summary>
    /// C8 Lab 3: POST with an Idempotency-Key. While the first request with the key is still running (a double click),
    /// the API answers 409 with Retry-After: wait and ask again with the same key until the stored answer comes back.
    /// </summary>
    private async Task<HttpResponseMessage> PostIdempotentAsync(string path, HttpContent? content, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        var body = content is null ? null : await content.ReadAsByteArrayAsync(cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
            request.Headers.Add("Idempotency-Key", idempotencyKey.ToString());
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = content!.Headers.ContentType;
            }

            var response = await http.SendAsync(request, cancellationToken);
            await response.Content.LoadIntoBufferAsync(cancellationToken); // readable again by the caller
            if (attempt == 20 || response.StatusCode != System.Net.HttpStatusCode.Conflict
                || !(await response.Content.ReadAsStringAsync(cancellationToken)).Contains("idempotency-in-progress", StringComparison.Ordinal))
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
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
