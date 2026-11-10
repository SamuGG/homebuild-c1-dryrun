using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

public sealed class ErrorTests : IDisposable
{
    private const string Plot1 = EndpointTests.RiversidePlot1;
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public ErrorTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Invalid_reserve_request_returns_400_with_field_errors()
    {
        var response = await _client.PostAsJsonAsync(
            $"/units/{Plot1}/reservations", new { buyerId = Guid.NewGuid(), deposit = -5, currency = "pounds" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblem(response);
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Deposit", out _) || errors.TryGetProperty("deposit", out _), errors.ToString());
        Assert.True(errors.TryGetProperty("Currency", out _) || errors.TryGetProperty("currency", out _), errors.ToString());
    }

    [Fact]
    public async Task Unit_unavailable_returns_problem_with_type_uri()
    {
        await _client.PostAsJsonAsync($"/units/{Plot1}/reservations", EndpointTests.Deposit(5000), Ct);

        var response = await _client.PostAsJsonAsync($"/units/{Plot1}/reservations", EndpointTests.Deposit(5000), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblem(response);
        Assert.Equal("https://homebuild.dev/problems/unit-unavailable", problem.GetProperty("type").GetString());
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Unhandled_exception_returns_500_problem_without_details()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ICatalogueQueries, ExplodingCatalogue>()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/units/{Plot1}", Ct);
        var reserve = await client.PostAsJsonAsync($"/units/{Plot1}/reservations", EndpointTests.Deposit(5000), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // catalogue reads use the store, not ICatalogueQueries
        Assert.Equal(HttpStatusCode.InternalServerError, reserve.StatusCode);
        var body = await reserve.Content.ReadAsStringAsync(Ct);
        var problem = JsonElement.Parse(body);
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.DoesNotContain(ExplodingCatalogue.Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_problem_has_traceId()
    {
        await _client.PostAsJsonAsync($"/units/{Plot1}/reservations", EndpointTests.Deposit(5000), Ct);
        using var exploding = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<ICatalogueQueries, ExplodingCatalogue>()));
        using var explodingClient = exploding.CreateClient();

        HttpResponseMessage[] responses =
        [
            await _client.PostAsJsonAsync($"/units/{Plot1}/reservations", new { buyerId = Guid.NewGuid(), deposit = -1, currency = "x" }, Ct), // 400 validation
            await _client.GetAsync($"/units/{Guid.Empty}", Ct), // 400 filter
            await _client.GetAsync("/developments?page=0", Ct), // 400 paging
            await _client.GetAsync($"/units/{Guid.NewGuid()}", Ct), // 404
            await _client.PostAsJsonAsync($"/units/{Plot1}/reservations", EndpointTests.Deposit(5000), Ct), // 409
            await _client.PostAsJsonAsync($"/units/{EndpointTests.RiversidePlot2}/reservations", EndpointTests.Deposit(1), Ct), // 422
            await explodingClient.PostAsJsonAsync($"/units/{Plot1}/reservations", EndpointTests.Deposit(5000), Ct), // 500
        ];

        foreach (var response in responses)
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await ReadProblem(response);
            Assert.True(problem.TryGetProperty("traceId", out var traceId) && traceId.GetString() is { Length: > 0 },
                $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {problem}");
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> ReadProblem(HttpResponseMessage response) =>
        JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));

    private sealed class ExplodingCatalogue : ICatalogueQueries
    {
        public const string Secret = "connection string Server=db;Password=hunter2";

        public Unit? FindUnit(UnitId unitId) => throw new InvalidOperationException(Secret);

        public string DepositStrategyFor(DevelopmentId developmentId) => throw new InvalidOperationException(Secret);
    }
}
