using HomeBuild.Api.Modules.Accounts;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Modules.Sales;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Data;

/// <summary>Shared database setup: PostgreSQL, snake_case names. Each module's context passes its own schema.</summary>
public static class HomeBuildDatabase
{
    public const string ConnectionStringName = "HomeBuild";

    /// <summary>Each module keeps its own migrations history table inside its own schema.</summary>
    internal static DbContextOptionsBuilder UseHomeBuildDatabase(this DbContextOptionsBuilder options, string connectionString, string schema) =>
        options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention();

    internal static string ConnectionString(IServiceProvider services) =>
        services.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is missing.");

    /// <summary>Applies every module's migrations (and seed data) to the database. For tests and tools, never app startup.</summary>
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using (var catalogue = CatalogueModule.CreateDbContext(connectionString))
        {
            await catalogue.Database.MigrateAsync(cancellationToken);
        }

        await using (var sales = SalesModule.CreateDbContext(connectionString))
        {
            await sales.Database.MigrateAsync(cancellationToken);
        }

        await using (var accounts = AccountsModule.CreateDbContext(connectionString))
        {
            await accounts.Database.MigrateAsync(cancellationToken);
        }

        await using var construction = ConstructionModule.CreateDbContext(connectionString);
        await construction.Database.MigrateAsync(cancellationToken);
    }
}
