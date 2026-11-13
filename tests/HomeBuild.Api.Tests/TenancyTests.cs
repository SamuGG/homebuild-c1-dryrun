using static HomeBuild.Api.Tests.CatalogueBuilders;
using static HomeBuild.Testing.Builders;
using System.Net;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HomeBuild.Api.Tests;

[Collection(IntegrationDatabase.Name)]
public sealed class TenancyTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tenant_A_cannot_read_tenant_B_units_through_the_API()
    {
        var cs = await database.CreateDatabaseAsync();
        var otherUnit = await AddOtherTenantUnitAsync(cs);
        await using var factory = new ApiFactory(cs);
        using var demo = factory.CreateClient();
        using var other = factory.CreateClient();
        // Since C4 Lab 5 the tenant comes from the user's membership, not the header.
        other.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Access(TestTokens.Users.OtherTenantBuyer.ToString(), "buyer"));

        Assert.Equal(HttpStatusCode.NotFound, (await demo.GetAsync($"/v1/units/{otherUnit}", Ct)).StatusCode);
        Assert.DoesNotContain(otherUnit.ToString(), await demo.GetStringAsync("/v1/units/search?q=other", Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/v1/units/{otherUnit}", Ct)).StatusCode);
    }

    [Fact]
    public async Task IgnoreQueryFilters_still_returns_only_current_tenant_rows()
    {
        var cs = await database.CreateDatabaseAsync();
        await AddOtherTenantUnitAsync(cs);
        await using var db = CatalogueModule.CreateDbContext(PostgresFixture.AppConnectionString(cs), TestTenants.Demo);

        var tenants = await db.Units.IgnoreQueryFilters().Select(u => EF.Property<Guid>(u, "TenantId")).Distinct().ToListAsync(Ct);
        var rawCount = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM catalogue.units").SingleAsync(Ct);

        Assert.Equal([TestTenants.Demo], tenants); // RLS catches what the missing filter would leak
        Assert.Equal(6, rawCount); // raw SQL skips EF's filters too
    }

    [Fact]
    public async Task Missing_tenant_setting_returns_no_rows()
    {
        var cs = await database.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(PostgresFixture.AppConnectionString(cs));
        await connection.OpenAsync(Ct);

        Assert.Equal(0L, await CountAsync(connection, "catalogue.units"));
        Assert.Equal(0L, await CountAsync(connection, "sales.units"));
    }

    [Fact]
    public async Task Pooled_connection_does_not_keep_previous_tenant()
    {
        var cs = await database.CreateDatabaseAsync();
        var pooled = new NpgsqlConnectionStringBuilder(PostgresFixture.AppConnectionString(cs)) { MaxPoolSize = 1 }.ToString();
        await using (var db = CatalogueModule.CreateDbContext(pooled, TestTenants.Demo))
        {
            Assert.Equal(6, await db.Units.CountAsync(Ct)); // the tenant was set on this physical connection
        }

        await using var connection = new NpgsqlConnection(pooled); // the same physical connection, back from the pool
        await connection.OpenAsync(Ct);

        Assert.Equal(0L, await CountAsync(connection, "catalogue.units"));
    }

    private static async Task<UnitId> AddOtherTenantUnitAsync(string ownerCs)
    {
        var development = DevelopmentId.New();
        var unit = AUnit().InDevelopment(development).WithPlot("B1").Build();
        await using var db = CatalogueModule.CreateDbContext(ownerCs, TestTenants.Other);
        db.Developments.Add(ADevelopment().WithId(development).Named("Other tenant").Build());
        var entry = db.Units.Add(unit);
        entry.Property(CatalogueDbContext.UnitName).CurrentValue = "Other tenant plot";
        await db.SaveChangesAsync(Ct);
        return unit.Id;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
