using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HomeBuild.Api.Tests;

public sealed class EndpointTests(PostgresFixture database) : IAsyncLifetime
{
    internal const string RiversidePlot1 = "0198a000-0000-7000-8001-000000000001";
    internal const string RiversidePlot2 = "0198a000-0000-7000-8001-000000000002";

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

    internal static object Deposit(decimal amount) => new { buyerId = Guid.NewGuid(), deposit = amount, currency = "GBP" };

    [Fact]
    public async Task Reserve_available_unit_returns_201_with_location()
    {
        var response = await _client.PostAsJsonAsync($"/v1/units/{RiversidePlot1}/reservations", Deposit(5000), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var read = await _client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Reserve_reserved_unit_returns_409()
    {
        await _client.PostAsJsonAsync($"/v1/units/{RiversidePlot1}/reservations", Deposit(5000), TestContext.Current.CancellationToken);

        var second = await _client.PostAsJsonAsync($"/v1/units/{RiversidePlot1}/reservations", Deposit(5000), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Reserve_with_low_deposit_returns_422()
    {
        var response = await _client.PostAsJsonAsync($"/v1/units/{RiversidePlot2}/reservations", Deposit(1), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_is_idempotent_and_returns_204_twice()
    {
        var created = await _client.PostAsJsonAsync($"/v1/units/{RiversidePlot1}/reservations", Deposit(5000), TestContext.Current.CancellationToken);
        var location = created.Headers.Location!;

        var first = await _client.DeleteAsync(location, TestContext.Current.CancellationToken);
        var second = await _client.DeleteAsync(location, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var unit = await _client.GetFromJsonAsync<UnitView>($"/v1/units/{RiversidePlot1}", TestContext.Current.CancellationToken);
        Assert.Equal("Available", unit!.Status);
    }

    [Fact]
    public async Task Default_unit_id_is_rejected_by_filter_with_400()
    {
        var empty = Guid.Empty;

        var get = await _client.GetAsync($"/v1/units/{empty}", TestContext.Current.CancellationToken);
        var post = await _client.PostAsJsonAsync($"/v1/units/{empty}/reservations", Deposit(5000), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    private sealed record UnitView(Guid Id, string Status);
}
