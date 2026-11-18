using Microsoft.EntityFrameworkCore.Design;

namespace HomeBuild.Workers;

/// <summary>For `dotnet ef migrations add` and bundles: a placeholder connection (migrations are generated offline;
/// bundles and `database update` pass the real one with --connection).</summary>
internal sealed class DesignTimeContractsDbContextFactory : IDesignTimeDbContextFactory<ContractsDbContext>
{
    public ContractsDbContext CreateDbContext(string[] args) =>
        ContractsDbContext.Create(Environment.GetEnvironmentVariable("ConnectionStrings__HomeBuild") ?? "Host=localhost;Database=homebuild;Username=design-time");
}
