using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HomeBuild.Api.Tests;

/// <summary>C5 Lab 5: the staff unit edit form's endpoint, PUT /v2/units/{id}.</summary>
[Collection(IntegrationDatabase.Name)]
[AsStaff("admin")]
public sealed class EditUnitTests(PostgresFixture database) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await database.CreateApiAsync();
        _admin = _factory.CreateClient(); // signed in as admin by [AsStaff("admin")]
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Admin_changes_price_and_description()
    {
        var url = $"/v2/units/{EndpointTests.RiversidePlot3}";

        var put = await _admin.PutAsJsonAsync(url, new { price = 289500, description = "South-facing garden" }, Ct);
        var unit = JsonElement.Parse(await _admin.GetStringAsync(url, Ct));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(289500m, unit.GetProperty("price").GetProperty("amount").GetDecimal());
        Assert.Equal("GBP", unit.GetProperty("price").GetProperty("currency").GetString());
        Assert.Equal("South-facing garden", unit.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Invalid_price_and_description_are_field_errors()
    {
        var put = await _admin.PutAsJsonAsync($"/v2/units/{EndpointTests.RiversidePlot3}",
            new { price = 0, description = new string('x', 2001) }, Ct);
        var errors = JsonElement.Parse(await put.Content.ReadAsStringAsync(Ct)).GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(["Description", "Price"], errors.EnumerateObject().Select(e => e.Name).Order());
    }

    [Fact]
    public async Task Unknown_unit_is_not_found_and_v1_has_no_edit()
    {
        var unknown = await _admin.PutAsJsonAsync("/v2/units/0198a000-0000-7000-8001-0000000000ff", new { price = 1000, description = "" }, Ct);
        var v1 = await _admin.PutAsJsonAsync($"/v1/units/{EndpointTests.RiversidePlot3}", new { price = 1000, description = "" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, v1.StatusCode);
    }
}
