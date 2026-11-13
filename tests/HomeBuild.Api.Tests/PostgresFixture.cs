using HomeBuild.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Testcontainers.PostgreSql;

namespace HomeBuild.Api.Tests;

/// <summary>
/// The integration tests that use the database: one collection, so they share one <see cref="PostgresFixture"/> and
/// run one at a time (each test truncates the shared database before it starts).
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationDatabase : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Database";
}

/// <summary>
/// One PostgreSQL 18 container and one database for the test run (C6 Lab 3). Migrations run once, with the same
/// module-by-module logic as the migration bundles (<see cref="HomeBuildDatabase.MigrateAsync"/>). Before each test the
/// data is reset by truncating every module table and re-running the seed, not by recreating the database.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Migration tests still create their own databases, and each ApiFactory has its own connection pool.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18")
        .WithCommand("-c", "max_connections=500")
        .Build();

    private string _shared = null!;
    private string _truncate = null!;

    // Throwaway password for the test container's application role.
    private const string AppPassword = "app-test";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        // The migration creates homebuild_app NOLOGIN when it is missing; each environment gives it a login.
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE ROLE homebuild_app LOGIN PASSWORD '{AppPassword}'", connection);
        await create.ExecuteNonQueryAsync();

        // The shared database, migrated once.
        _shared = await CreateEmptyDatabaseAsync();
        await HomeBuildDatabase.MigrateAsync(_shared);
        await using var tables = new NpgsqlConnection(_shared);
        await tables.OpenAsync();
        await using var list = new NpgsqlCommand(
            """
            SELECT string_agg(format('%I.%I', schemaname, tablename), ', ' ORDER BY schemaname, tablename)
            FROM pg_tables
            WHERE schemaname IN ('catalogue', 'sales', 'accounts', 'construction') AND tablename <> '__ef_migrations_history'
            """, tables);
        _truncate = $"TRUNCATE {await list.ExecuteScalarAsync()} RESTART IDENTITY CASCADE";
        await SeedAsync(_shared);
    }

    /// <summary>The tables the reset truncates (for the log and for tests of the fixture itself).</summary>
    public string TruncateStatement => _truncate;

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public string AdminConnectionString => _container.GetConnectionString();

    /// <summary>Creates an empty database (no migrations).</summary>
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name }.ToString();
    }

    /// <summary>
    /// The shared database, reset for the calling test: every module table truncated, then the fixed seed data and
    /// the test users' memberships written again. Tests in the <see cref="IntegrationDatabase"/> run one at a time.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        await using (var connection = new NpgsqlConnection(_shared))
        {
            await connection.OpenAsync();
            await using var truncate = new NpgsqlCommand(_truncate, connection);
            await truncate.ExecuteNonQueryAsync();
        }

        await SeedAsync(_shared);
        return _shared;
    }

    public async Task<ApiFactory> CreateApiAsync() => new(await CreateDatabaseAsync());

    // The modules' seeding runs inside MigrateAsync (EF Core's UseSeeding) and only fills empty tables, so after a
    // truncation it restores the fixed data; with nothing pending, MigrateAsync migrates nothing.
    private static async Task SeedAsync(string connectionString)
    {
        await HomeBuildDatabase.MigrateAsync(connectionString);
        await using var accounts = HomeBuild.Api.Modules.Accounts.AccountsModule.CreateDbContext(connectionString);
        foreach (var (subject, tenant, roles) in TestTokens.Users.Memberships())
        {
            accounts.TenantMemberships.Add(new HomeBuild.Api.Modules.Accounts.TenantMembership(subject.ToString(), tenant, roles));
        }

        await accounts.SaveChangesAsync();
    }

    /// <summary>The connection string the application uses: the non-owner role that row-level security applies to.</summary>
    public static string AppConnectionString(string ownerConnectionString) =>
        new NpgsqlConnectionStringBuilder(ownerConnectionString) { Username = "homebuild_app", Password = AppPassword }.ToString();
}

/// <summary>The tenants used by tests. C2's fixed data belongs to <see cref="Demo"/>.</summary>
public static class TestTenants
{
    public static readonly Guid Demo = Guid.Parse("0198a000-0000-7000-8000-00000000000a");
    public static readonly Guid Other = Guid.Parse("0198a000-0000-7000-8000-00000000000b");
}

/// <summary>The API, pointed at one test database. Clients send <see cref="TestTenants.Demo"/> unless told otherwise.</summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public string ConnectionString { get; } = PostgresFixture.AppConnectionString(connectionString);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("ConnectionStrings:" + HomeBuildDatabase.ConnectionStringName, ConnectionString);
        builder.UseSetting("Ssm:Enabled", "false"); // tests never read SSM, even as Production
        foreach (var (key, value) in TestTokens.Settings())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddFakeAuthentication());
    }

    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenants.Demo.ToString());
        // The user the test asked for with [AsBuyer]/[AsStaff] (fake authentication, no token); otherwise signed in
        // as the default buyer with a real token from the C4 test issuer, so JWT validation stays covered.
        if (FakeAuthentication.Requested() is { } user)
        {
            client.DefaultRequestHeaders.Add(FakeAuthentication.Header, FakeAuthentication.HeaderValue(user));
        }
        else
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Access(TestTokens.DefaultBuyer.ToString(), "buyer"));
        }
    }
}
