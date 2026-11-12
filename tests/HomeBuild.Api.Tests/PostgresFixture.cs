using HomeBuild.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(HomeBuild.Api.Tests.PostgresFixture))]

namespace HomeBuild.Api.Tests;

/// <summary>One PostgreSQL 18 container for the test run; every test gets its own freshly migrated database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Every test has its own database and connection pools, and tests run in parallel: the default
    // max_connections (100) ran out at about 110 tests ("53300: sorry, too many clients already").
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18")
        .WithCommand("-c", "max_connections=500")
        .Build();

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
    }

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

    /// <summary>Creates a database with every module's migrations and seed data applied.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await HomeBuildDatabase.MigrateAsync(connectionString);
        return connectionString;
    }

    public async Task<ApiFactory> CreateApiAsync() => new(await CreateDatabaseAsync());

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
        foreach (var (key, value) in TestTokens.Settings())
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenants.Demo.ToString());
        // Signed in by default, as a buyer: C2/C3 tests call endpoints that now require a user.
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Access(TestTokens.DefaultBuyer.ToString(), "buyer"));
    }
}
