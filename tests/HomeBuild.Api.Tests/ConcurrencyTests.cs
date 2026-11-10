using System.Net;
using System.Net.Http.Json;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HomeBuild.Api.Tests;

public sealed class ConcurrencyTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exactly_one_of_twenty_parallel_reservations_wins()
    {
        var cs = await database.CreateDatabaseAsync();
        await using var instanceA = new ApiFactory(cs);
        await using var instanceB = new ApiFactory(cs);
        using var clientA = instanceA.CreateClient();
        using var clientB = instanceB.CreateClient();
        using var start = new SemaphoreSlim(0);

        // 10 per instance: each instance's rate limiter allows 10 reservations a minute per client.
        var requests = Enumerable.Range(0, 20).Select(async i =>
        {
            await start.WaitAsync(Ct);
            var client = i % 2 == 0 ? clientA : clientB;
            var response = await client.PostAsJsonAsync($"/v1/units/{EndpointTests.RiversidePlot1}/reservations", EndpointTests.Deposit(5000), Ct);
            return response.StatusCode;
        }).ToList();
        start.Release(20);
        var statuses = await Task.WhenAll(requests);

        var activeReservations = await CountActiveReservationsAsync(cs, EndpointTests.RiversidePlot1);
        var summary = string.Join(", ", statuses.GroupBy(s => s).Select(g => $"{(int)g.Key}×{g.Count()}"));
        Assert.True(statuses.Count(s => s == HttpStatusCode.Created) == 1, $"statuses: {summary}; active reservations in DB: {activeReservations}");
        Assert.All(statuses.Where(s => s != HttpStatusCode.Created), s => Assert.Equal(HttpStatusCode.Conflict, s));
        Assert.Equal(1, activeReservations);
    }

    [Fact]
    public async Task Second_active_reservation_violates_unique_index()
    {
        var cs = await database.CreateDatabaseAsync();
        var unitId = new UnitId(Guid.Parse(EndpointTests.RiversidePlot1));
        var now = DateTimeOffset.UtcNow;
        await using (var db = SalesModule.CreateDbContext(cs))
        {
            var unit = await db.Units.SingleAsync(u => u.Id == unitId, Ct);
            var first = Assert.IsType<Reserved>(unit.Reserve(BuyerId.New(), new Money(5000m, "GBP"),
                new ReservationPolicy(TimeSpan.FromDays(7), new Money(1000m, "GBP")), now).Value);
            db.Reservations.Add(first.Reservation);
            await db.SaveChangesAsync(Ct);
        }

        // A second write path that never touches the unit, so its version check cannot help.
        await using var other = SalesModule.CreateDbContext(cs);
        other.Reservations.Add(new Reservation(ReservationId.New(), unitId, BuyerId.New(), new Money(5000m, "GBP"), now, now.AddDays(7)));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync(Ct));
        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(SalesDbContext.ActiveReservationIndex, postgres.ConstraintName);
    }

    [Fact]
    public async Task Concurrency_conflict_returns_409()
    {
        var cs = await database.CreateDatabaseAsync();
        await using var factory = new ApiFactory(cs);
        // Another instance updates the unit between this request's read and its write.
        await using var racing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.ConfigureDbContext<SalesDbContext>(o => o.AddInterceptors(new TouchUnitBeforeSave(cs, EndpointTests.RiversidePlot1)))));
        using var client = racing.CreateClient();

        var response = await client.PostAsJsonAsync($"/v1/units/{EndpointTests.RiversidePlot1}/reservations", EndpointTests.Deposit(5000), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("https://homebuild.dev/problems/unit-unavailable", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(0, await CountActiveReservationsAsync(cs, EndpointTests.RiversidePlot1));
    }

    private sealed class TouchUnitBeforeSave(string connectionString, string unitId) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var touch = new NpgsqlCommand("UPDATE sales.units SET plot_number = plot_number WHERE id = @id", connection);
            touch.Parameters.AddWithValue("id", Guid.Parse(unitId));
            await touch.ExecuteNonQueryAsync(cancellationToken); // new row version: xmin changes
            return result;
        }
    }

    internal static async Task<long> CountActiveReservationsAsync(string cs, string unitId)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM sales.reservations WHERE unit_id = @unit AND split_part(state, '|', 1) IN ('Held', 'Confirmed')", connection);
        command.Parameters.AddWithValue("unit", Guid.Parse(unitId));
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
