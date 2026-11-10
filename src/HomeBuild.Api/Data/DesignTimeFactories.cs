using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Sales;
using Microsoft.EntityFrameworkCore.Design;

namespace HomeBuild.Api.Data;

// dotnet ef and migration bundles build contexts through these factories instead of the web host, so they need
// no appsettings or environment. Bundles replace the placeholder with --connection; `dotnet ef` commands read
// ConnectionStrings__HomeBuild, falling back to the local compose database.
internal static class DesignTime
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__HomeBuild")
        ?? "Host=localhost;Port=5432;Database=homebuild;Username=homebuild;Password=homebuild-dev";
}

internal sealed class CatalogueDesignTimeFactory : IDesignTimeDbContextFactory<CatalogueDbContext>
{
    public CatalogueDbContext CreateDbContext(string[] args) => CatalogueModule.CreateDbContext(DesignTime.ConnectionString);
}

internal sealed class SalesDesignTimeFactory : IDesignTimeDbContextFactory<SalesDbContext>
{
    public SalesDbContext CreateDbContext(string[] args) => SalesModule.CreateDbContext(DesignTime.ConnectionString);
}
