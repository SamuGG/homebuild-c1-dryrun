using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HomeBuild.Api.Tests;

[Collection(IntegrationDatabase.Name)]
public sealed class TokenValidationTests(PostgresFixture database) : IAsyncLifetime
{
    private const string AnyReservation = "/v1/reservations/0198a000-0000-7000-8000-0000000000ff";
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await database.CreateApiAsync();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = null; // each test chooses its token
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Expired_token_returns_401()
    {
        // Expired well beyond the default 5-minute clock skew.
        var token = TestTokens.Create("buyer-1", ["buyer"], expires: DateTime.UtcNow.AddMinutes(-10));

        var response = await SendAsync(AnyReservation, token);

        await AssertUnauthorizedProblemAsync(response);
        Assert.Contains("expired", response.Headers.WwwAuthenticate.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Token_from_other_client_returns_401()
    {
        var token = TestTokens.Create("buyer-1", ["buyer"], clientId: TestTokens.OtherClientId);

        await AssertUnauthorizedProblemAsync(await SendAsync(AnyReservation, token));
    }

    [Fact]
    public async Task Id_token_used_as_access_token_returns_401()
    {
        // A Cognito ID token: token_use = id, aud = the client, no client_id.
        var token = TestTokens.Create("buyer-1", ["buyer"], tokenUse: "id", clientId: null, audience: TestTokens.ClientId);

        await AssertUnauthorizedProblemAsync(await SendAsync(AnyReservation, token));
    }

    [Fact]
    public async Task Buyer_token_has_buyer_role()
    {
        var response = await SendAsync("/v1/me", TestTokens.Access("buyer-1", "buyer"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("buyer-1", me.GetProperty("subject").GetString());
        Assert.Equal(["buyer"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Catalogue_reads_are_anonymous()
    {
        foreach (var path in new[] { "/v1/developments", $"/v1/units/{EndpointTests.RiversidePlot1}", "/v1/units/search?q=river",
                     "/v2/developments/0198a000-0000-7000-8000-000000000001/units" })
        {
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(path, token: null)).StatusCode);
        }

        await AssertUnauthorizedProblemAsync(await SendAsync(AnyReservation, token: null));
        await AssertUnauthorizedProblemAsync(await SendAsync("/v1/developments/0198a000-0000-7000-8000-000000000001/page", token: null));
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, Ct);
    }

    private static async Task AssertUnauthorizedProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }
}
