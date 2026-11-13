using static HomeBuild.Api.Tests.CatalogueBuilders;
using static HomeBuild.Testing.Builders;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using Npgsql;

namespace HomeBuild.Api.Tests;

[Collection(IntegrationDatabase.Name)]
public sealed class TenantClaimTests(PostgresFixture database) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await database.CreateApiAsync();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Remove("X-Tenant-Id");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Header_tenant_is_ignored()
    {
        // A tenant-B buyer forges tenant A (the demo tenant) in the header.
        using var request = Request(HttpMethod.Get, $"/v1/units/{EndpointTests.RiversidePlot1}", TestTokens.Users.OtherTenantBuyer);
        request.Headers.Add("X-Tenant-Id", TestTenants.Demo.ToString());
        var forged = await _client.SendAsync(request, Ct);

        using var me = Request(HttpMethod.Get, "/v1/me", TestTokens.Users.OtherTenantBuyer);
        me.Headers.Add("X-Tenant-Id", TestTenants.Demo.ToString());
        var identity = JsonElement.Parse(await (await _client.SendAsync(me, Ct)).Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode); // tenant A's unit stays invisible
        Assert.Equal(TestTenants.Other, identity.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task User_without_membership_gets_403()
    {
        using var request = Request(HttpMethod.Get, "/v1/developments/0198a000-0000-7000-8000-000000000001/page", TestTokens.Users.NoMembership);
        request.Headers.Add("X-Tenant-Id", TestTenants.Demo.ToString()); // a header does not help

        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("https://homebuild.dev/problems/no-membership", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Tenant_A_buyer_token_cannot_reach_tenant_B_data()
    {
        // Tenant B data: a development, a unit and a reservation by tenant B's buyer.
        var (developmentB, unitB) = await AddTenantBUnitAsync();
        using var reserveB = Request(HttpMethod.Post, $"/v1/units/{unitB}/reservations", TestTokens.Users.OtherTenantBuyer,
            new { deposit = 5000, currency = "GBP" });
        var createdB = await _client.SendAsync(reserveB, Ct);
        Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);
        var reservationB = createdB.Headers.Location!.ToString();

        // Through the API, as tenant A's buyer (with tenant B forged in the header, for good measure).
        var tenantA = TestTokens.Users.Owner;
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, reservationB, null),
                     (HttpMethod.Get, reservationB + "/deposit", null),
                     (HttpMethod.Delete, reservationB, null),
                     (HttpMethod.Get, $"/v1/units/{unitB}", null),
                     (HttpMethod.Get, $"/v1/developments/{developmentB}/page", null),
                     (HttpMethod.Post, $"/v1/units/{unitB}/reservations", new { deposit = 5000, currency = "GBP" }),
                 })
        {
            using var request = Request(method, path, tenantA, body);
            request.Headers.Add("X-Tenant-Id", TestTenants.Other.ToString());
            var response = await _client.SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {response.StatusCode}");
        }

        using var list = Request(HttpMethod.Get, "/v1/developments?size=100", tenantA);
        Assert.DoesNotContain(developmentB.ToString(), await (await _client.SendAsync(list, Ct)).Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // And in the database, as the API's role with tenant A's setting: RLS shows none of tenant B's rows.
        await using var connection = new NpgsqlConnection(PostgresFixture.AppConnectionString(_factory.ConnectionString));
        await connection.OpenAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await using var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{TestTenants.Demo}', true)", connection, tx);
        await set.ExecuteScalarAsync(Ct);
        await using var count = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM sales.reservations WHERE tenant_id = @b) + (SELECT count(*) FROM catalogue.units WHERE tenant_id = @b)",
            connection, tx);
        count.Parameters.AddWithValue("b", TestTenants.Other);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
    }

    private async Task<(Guid Development, Guid Unit)> AddTenantBUnitAsync()
    {
        var development = DevelopmentId.New();
        var unit = AUnit().InDevelopment(development).WithPlot("B1").Build();
        await using (var catalogue = CatalogueModule.CreateDbContext(_factory.ConnectionString, TestTenants.Other))
        {
            catalogue.Developments.Add(ADevelopment().WithId(development).Named("Tenant B development").Build());
            catalogue.Units.Add(unit);
            await catalogue.SaveChangesAsync(Ct);
        }

        await using (var sales = HomeBuild.Api.Modules.Sales.SalesModule.CreateDbContext(_factory.ConnectionString, TestTenants.Other))
        {
            sales.Units.Add(AUnit().WithId(unit.Id).InDevelopment(development).WithPlot("B1").Build());
            await sales.SaveChangesAsync(Ct);
        }

        return (development.Value, unit.Id.Value);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid subject, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(subject.ToString(), "buyer"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}
