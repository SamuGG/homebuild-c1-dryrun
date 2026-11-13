using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HomeBuild.Api.Tests;

/// <summary>C5 Lab 6: the staff build board and the buyer's progress come from different endpoints and DTOs.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class ConstructionTests(PostgresFixture database) : IAsyncLifetime
{
    private const string Riverside = "0198a000-0000-7000-8000-000000000001";

    // Everything the buyer view may contain. Anything else, at any depth, is a leak.
    private static readonly HashSet<string> BuyerFields =
    [
        "homes", "reservationId", "unitId", "plotNumber", "developmentName", "reservationState", "reservationExpiresAt",
        "progress", "stage", "stepsDone", "stepsTotal", "nextStep",
    ];

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await database.CreateApiAsync();
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Buyer_progress_response_has_no_internal_fields()
    {
        var owner = TestTokens.Users.Owner.ToString();
        using var reserve = Request(HttpMethod.Post, $"/v2/units/{EndpointTests.RiversidePlot1}/reservations", TestTokens.Access(owner, "buyer"));
        reserve.Content = JsonContent.Create(new { deposit = 5000, currency = "GBP" });
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(reserve, Ct)).StatusCode);

        using var request = Request(HttpMethod.Get, "/v2/me/progress", TestTokens.Access(owner, "buyer"));
        using var response = await _client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var home = Assert.Single(JsonElement.Parse(body).GetProperty("homes").EnumerateArray());
        Assert.Equal("P1", home.GetProperty("plotNumber").GetString());
        Assert.Equal("Riverside", home.GetProperty("developmentName").GetString());
        Assert.Equal(7, home.GetProperty("progress").GetProperty("stepsTotal").GetInt32());
        // Structure: only the buyer fields, at every level.
        var names = PropertyNames(JsonElement.Parse(body)).ToHashSet();
        Assert.Subset(BuyerFields, names);
        // Content: none of the staff-only values either.
        foreach (var internalValue in new[] { "Groundworks", "Timber", "Roofing", "Electrical", "inspection", "openDefects", "buyerId", "deposit" })
        {
            Assert.DoesNotContain(internalValue, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Buyer_without_reservations_sees_no_homes_and_never_another_buyers()
    {
        using var reserve = Request(HttpMethod.Post, $"/v2/units/{EndpointTests.RiversidePlot2}/reservations", TestTokens.Access(TestTokens.Users.OtherBuyer.ToString(), "buyer"));
        reserve.Content = JsonContent.Create(new { deposit = 5000, currency = "GBP" });
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(reserve, Ct)).StatusCode);

        using var request = Request(HttpMethod.Get, "/v2/me/progress", TestTokens.Access(TestTokens.Users.ThirdBuyer.ToString(), "buyer"));
        var body = await (await _client.SendAsync(request, Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Equal("""{"homes":[]}""", body);
    }

    [Fact]
    public async Task Site_staff_see_the_board_with_internal_fields()
    {
        using var request = Request(HttpMethod.Get, $"/v2/developments/{Riverside}/board", TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager"));
        using var response = await _client.SendAsync(request, Ct);
        var board = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Riverside", board.GetProperty("developmentName").GetString());
        Assert.Equal(7, board.GetProperty("milestones").GetArrayLength());
        var rows = board.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["P1", "P2", "P3"], rows.Select(r => r.GetProperty("plotNumber").GetString()));
        var cell = rows[0].GetProperty("cells")[0];
        Assert.True(cell.TryGetProperty("subcontractor", out _) && cell.TryGetProperty("siteNotes", out _) && cell.TryGetProperty("openDefects", out _));
        // Statuses are strings, like every other status in the API (the web app reads them as strings).
        Assert.Subset(new HashSet<string> { "NotStarted", "InProgress", "Done" },
            rows.SelectMany(r => r.GetProperty("cells").EnumerateArray()).Select(c => c.GetProperty("status").GetString()!).ToHashSet());
    }

    [Theory]
    [InlineData("site-manager", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("sales-agent", HttpStatusCode.Forbidden)]
    [InlineData("finance", HttpStatusCode.Forbidden)]
    [InlineData("aftercare", HttpStatusCode.Forbidden)]
    [InlineData("buyer", HttpStatusCode.Forbidden)]
    public async Task Board_is_for_site_staff_and_progress_for_buyers(string role, HttpStatusCode boardStatus)
    {
        var subject = role == "buyer" ? TestTokens.Users.ThirdBuyer : TestTokens.Users.Staff(role);
        using var board = Request(HttpMethod.Get, $"/v2/developments/{Riverside}/board", TestTokens.Access(subject.ToString(), role));
        using var progress = Request(HttpMethod.Get, "/v2/me/progress", TestTokens.Access(subject.ToString(), role));

        Assert.Equal(boardStatus, (await _client.SendAsync(board, Ct)).StatusCode);
        Assert.Equal(role == "buyer" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await _client.SendAsync(progress, Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_development_board_is_not_found_and_v1_has_no_board()
    {
        var token = TestTokens.Access(TestTokens.Users.Staff("admin").ToString(), "admin");
        using var unknown = Request(HttpMethod.Get, "/v2/developments/0198a000-0000-7000-8000-0000000000ff/board", token);
        using var v1 = Request(HttpMethod.Get, $"/v1/developments/{Riverside}/board", token);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(unknown, Ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await _client.SendAsync(v1, Ct)).StatusCode);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };
}
