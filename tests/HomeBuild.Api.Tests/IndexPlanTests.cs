using System.Text.RegularExpressions;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HomeBuild.Api.Tests;

[Collection(IntegrationDatabase.Name)]
public sealed partial class IndexPlanTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_hot_query_plan_uses_its_intended_index()
    {
        var cs = await database.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(Ct);
        // Enough rows that the planner prefers an index: 100 developments × 200 units, 4,000 held reservations
        // over 1,000 buyers, expiring across the next 7 days.
        await ExecuteAsync(connection, """
            INSERT INTO sales.units (id, tenant_id, development_id, plot_number, status, price_amount, price_currency)
            SELECT gen_random_uuid(), '0198a000-0000-7000-8000-00000000000a', ('00000000-0000-4000-8000-' || lpad((g % 100)::text, 12, '0'))::uuid, 'P' || g,
                   CASE WHEN g % 10 = 0 THEN 'Reserved' ELSE 'Available' END, 250000, 'GBP'
            FROM generate_series(1, 20000) g;
            INSERT INTO sales.reservations (id, tenant_id, unit_id, buyer_id, created_at, expires_at, state, deposit_amount, deposit_currency)
            SELECT gen_random_uuid(), u.tenant_id, u.id, ('00000000-0000-4000-9000-' || lpad((row_number() OVER () % 1000)::text, 12, '0'))::uuid,
                   now() - (row_number() OVER ()) * interval '2 minutes', now() + (row_number() OVER ()) * interval '2 minutes',
                   'Held|2026-11-17T00:00:00.0000000+00:00', 5000, 'GBP'
            FROM sales.units u WHERE u.plot_number LIKE 'P%' LIMIT 4000;
            ANALYZE sales.units;
            ANALYZE sales.reservations;
            """);
        await using var db = SalesModule.CreateDbContext(cs, TestTenants.Demo);
        var now = DateTimeOffset.UtcNow;
        var development = new DevelopmentId(Guid.Parse("00000000-0000-4000-8000-000000000007"));
        var buyer = new BuyerId(Guid.Parse("00000000-0000-4000-9000-000000000042"));

        var plans = new Dictionary<string, string>
        {
            ["ix_units_development_id_status"] = await ExplainAsync(connection, HotQueries.UnitsByStatus(db, development, UnitStatus.Available)),
            ["ix_reservations_held_expires_at"] = await ExplainAsync(connection, HotQueries.ExpiringBetween(db, now, now.AddHours(1))),
            ["ix_reservations_buyer_id_created_at"] = await ExplainAsync(connection, HotQueries.ForBuyer(db, buyer)),
        };

        Assert.All(plans, p => Assert.True(p.Value.Contains(p.Key, StringComparison.Ordinal), $"expected {p.Key} in:\n{p.Value}"));
    }

    /// <summary>EXPLAINs the exact SQL EF Core generates, with its parameter values inlined from ToQueryString's header.</summary>
    private static async Task<string> ExplainAsync<T>(NpgsqlConnection connection, IQueryable<T> query)
    {
        var text = query.ToQueryString();
        var sql = string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("--", StringComparison.Ordinal)));
        foreach (Match p in Parameter().Matches(text))
        {
            sql = sql.Replace("@" + p.Groups["name"].Value, $"'{p.Groups["value"].Value}'", StringComparison.Ordinal);
        }

        await using var command = new NpgsqlCommand("EXPLAIN " + sql, connection);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    [GeneratedRegex("^-- @(?<name>\\w+)='(?<value>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex Parameter();
}
