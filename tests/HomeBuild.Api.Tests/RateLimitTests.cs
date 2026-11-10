using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HomeBuild.Api.Tests;

public sealed class RateLimitTests(PostgresFixture database) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

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
    public async Task Eleventh_reservation_in_a_minute_returns_429()
    {
        var statuses = await Reserve(11);

        Assert.All(statuses.Take(10), s => Assert.NotEqual(HttpStatusCode.TooManyRequests, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    [Fact]
    public async Task Rejection_429_includes_Retry_After() // doc name 429_includes_Retry_After is not a valid C# identifier
    {
        await Reserve(10);

        var response = await _client.PostAsJsonAsync($"/v1/units/{EndpointTests.RiversidePlot1}/reservations", EndpointTests.Deposit(5000), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Reads_are_not_limited()
    {
        for (var i = 0; i < 30; i++)
        {
            var response = await _client.GetAsync($"/v1/units/{EndpointTests.RiversidePlot1}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private async Task<List<HttpStatusCode>> Reserve(int count)
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            var response = await _client.PostAsJsonAsync($"/v1/units/{EndpointTests.RiversidePlot1}/reservations", EndpointTests.Deposit(5000), TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }
}
