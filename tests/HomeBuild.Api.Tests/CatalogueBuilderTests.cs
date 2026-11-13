using HomeBuild.Api.Modules.Catalogue;
using Microsoft.EntityFrameworkCore;
using static HomeBuild.Api.Tests.CatalogueBuilders;
using static HomeBuild.Testing.Builders;

namespace HomeBuild.Api.Tests;

/// <summary>The API's aggregate (Development) has a builder whose default saves, with a unit, as is.</summary>
public sealed class CatalogueBuilderTests(PostgresFixture database)
{
    [Fact]
    public async Task Default_development_and_unit_save_and_load()
    {
        var cs = await database.CreateDatabaseAsync();
        var development = ADevelopment().Build();
        await using (var db = CatalogueModule.CreateDbContext(cs, TestTenants.Demo))
        {
            db.Developments.Add(development);
            db.Units.Add(AUnit().InDevelopment(development.Id).Build());
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = CatalogueModule.CreateDbContext(cs, TestTenants.Demo);
        Assert.Equal(1, await read.Units.CountAsync(u => u.DevelopmentId == development.Id, TestContext.Current.CancellationToken));
    }
}
