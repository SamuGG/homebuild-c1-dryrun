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
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

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
}

/// <summary>The API, pointed at one test database.</summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public string ConnectionString { get; } = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:" + HomeBuildDatabase.ConnectionStringName, ConnectionString);
}
