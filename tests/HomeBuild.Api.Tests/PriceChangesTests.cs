using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HomeBuild.Api.Tests;

/// <summary>C7 Lab 6, the comparison feature in Catalogue's style: one slice, tested through the API and the database.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class PriceChangesTests(PostgresFixture database)
{
    private const string Riverside = "0198a000-0000-7000-8000-000000000001";

    [Fact]
    public async Task Units_with_price_changes_are_listed_with_previous_and_current_price()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = await database.CreateApiAsync();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Staff("admin").ToString(), "admin"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/v2/units/{EndpointTests.RiversidePlot1}", new { price = 255_000, description = "" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/v2/units/{EndpointTests.RiversidePlot1}", new { price = 252_500, description = "" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/v2/units/{EndpointTests.RiversidePlot2}", new { price = 270_000, description = "same price" }, ct)).StatusCode);

        var changes = JsonElement.Parse(await admin.GetStringAsync($"/v2/developments/{Riverside}/price-changes", ct)).EnumerateArray().ToList();

        var change = Assert.Single(changes); // plot 2's price did not change
        Assert.Equal(("P1", 255_000m, 252_500m), (change.GetProperty("plotNumber").GetString(), change.GetProperty("previousPrice").GetDecimal(), change.GetProperty("currentPrice").GetDecimal()));
    }

    [Fact]
    public async Task Unknown_development_has_no_price_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v2/developments/0198a000-0000-7000-8000-0000000000ff/price-changes", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
