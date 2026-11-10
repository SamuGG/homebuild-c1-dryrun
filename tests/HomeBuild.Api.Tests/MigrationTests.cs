using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Sales;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Tests;

public sealed class MigrationTests
{
    [Fact]
    public void Model_has_no_pending_changes()
    {
        using var catalogue = CatalogueModule.CreateDbContext("Host=unused");
        using var sales = SalesModule.CreateDbContext("Host=unused");

        Assert.False(catalogue.Database.HasPendingModelChanges(), "CatalogueDbContext has model changes with no migration: run dotnet ef migrations add.");
        Assert.False(sales.Database.HasPendingModelChanges(), "SalesDbContext has model changes with no migration: run dotnet ef migrations add.");
    }
}
