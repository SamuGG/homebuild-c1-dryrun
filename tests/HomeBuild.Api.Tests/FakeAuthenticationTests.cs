using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HomeBuild.Api.Tests;

/// <summary>Users from test attributes (fake authentication), and the real JWT path for tests without one.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class FakeAuthenticationTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [AsBuyer(Tenant = "B")]
    public async Task Tenant_B_buyer_from_an_attribute_cannot_see_tenant_A_units()
    {
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();

        var me = JsonElement.Parse(await client.GetStringAsync("/v1/me", Ct));
        using var unit = await client.GetAsync($"/v1/units/{EndpointTests.RiversidePlot1}", Ct);

        Assert.Equal(TestTenants.Other, me.GetProperty("tenantId").GetGuid());
        Assert.Equal("fake-test-auth", me.GetProperty("clientId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unit.StatusCode); // RLS: tenant A's unit does not exist for tenant B
    }

    [Fact]
    [AsStaff("admin")]
    public async Task Admin_from_an_attribute_can_edit_a_unit()
    {
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();

        using var put = await client.PutAsJsonAsync($"/v2/units/{EndpointTests.RiversidePlot3}", new { price = 281_000, description = "Fake admin" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    [Fact]
    [AsBuyer]
    public async Task Buyer_from_an_attribute_is_forbidden_from_admin_endpoints()
    {
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();

        using var put = await client.PutAsJsonAsync($"/v2/units/{EndpointTests.RiversidePlot3}", new { price = 281_000, description = "" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Without_an_attribute_requests_go_through_real_JWT_validation()
    {
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();

        var me = JsonElement.Parse(await client.GetStringAsync("/v1/me", Ct));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        using var forged = await client.GetAsync("/v1/me", Ct);

        Assert.False(client.DefaultRequestHeaders.Contains(FakeAuthentication.Header));
        Assert.Equal(TestTokens.ClientId, me.GetProperty("clientId").GetString()); // issued by the C4 test issuer
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }
}
