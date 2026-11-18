using HomeBuild.Api.Modules.Accounts;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Api.Modules.Sales;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Data;

/// <summary>Shared database setup: PostgreSQL, snake_case names. Each module's context passes its own schema.</summary>
public static class HomeBuildDatabase
{
    public const string ConnectionStringName = "HomeBuild";

    /// <summary>
    /// Each module keeps its own migrations history table inside its own schema. C8 Lab 4: the Npgsql execution strategy
    /// retries transient failures (a dropped connection, a serialization failure, a failover). With it, every explicit
    /// transaction must run inside <c>Database.CreateExecutionStrategy().ExecuteAsync</c>, so a retry replays the whole
    /// unit of work; a BeginTransaction outside it fails fast with InvalidOperationException.
    /// </summary>
    internal static DbContextOptionsBuilder UseHomeBuildDatabase(this DbContextOptionsBuilder options, string connectionString, string schema) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", schema)
                .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null))
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

        await using (var construction = ConstructionModule.CreateDbContext(connectionString))
        {
            await construction.Database.MigrateAsync(cancellationToken);
        }

        await using (var payments = PaymentsModule.CreateDbContext(connectionString))
        {
            await payments.Database.MigrateAsync(cancellationToken);
        }

        // C8 Lab 3: idempotency keys, shared HTTP infrastructure (schema idempotency).
        await using var idempotency = HomeBuild.Api.Http.Idempotency.IdempotencyDbContext.Create(connectionString);
        await idempotency.Database.MigrateAsync(cancellationToken);
    }
}
