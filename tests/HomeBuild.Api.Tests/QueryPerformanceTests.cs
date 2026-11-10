using System.Net;
using System.Net.Http.Json;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Sales;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

public sealed class QueryPerformanceTests(PostgresFixture database)
{
    private const string Riverside = "0198a000-0000-7000-8000-000000000001";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Development_page_issues_at_most_three_queries()
    {
        var counter = new CommandCounter();
        await using var factory = new ApiFactory(await database.CreateDatabaseAsync());
        await using var counted = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.ConfigureDbContext<CatalogueDbContext>(o => o.AddInterceptors(counter));
            s.ConfigureDbContext<SalesDbContext>(o => o.AddInterceptors(counter));
        }));
        using var client = counted.CreateClient();
        await client.PostAsJsonAsync($"/v1/units/{EndpointTests.RiversidePlot1}/reservations", EndpointTests.Deposit(5000), Ct);
        counter.Reset();

        var response = await client.GetAsync($"/v1/developments/{Riverside}/page", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"latestReservation\":{", body, StringComparison.Ordinal);
        Assert.True(counter.Count <= 3, $"{counter.Count} queries:\n{string.Join("\n---\n", counter.Commands)}");
    }

    [Fact]
    public async Task Expiry_updates_in_one_statement()
    {
        var cs = await database.CreateDatabaseAsync();
        await using (var factory = new ApiFactory(cs))
        {
            using var client = factory.CreateClient();
            foreach (var unit in new[] { EndpointTests.RiversidePlot1, EndpointTests.RiversidePlot2 })
            {
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync($"/v1/units/{unit}/reservations", EndpointTests.Deposit(5000), Ct)).StatusCode);
            }
        }

        var counter = new CommandCounter();
        await using var db = new SalesDbContext((DbContextOptions<SalesDbContext>)SalesModule
            .Configure(new DbContextOptionsBuilder<SalesDbContext>(), cs).AddInterceptors(counter).Options);

        var expired = await new ReservationExpiry(db).ExpireStaleAsync(DateTimeOffset.UtcNow.AddDays(8), Ct);

        Assert.Equal(2, expired);
        var reservationUpdates = counter.Commands.Where(c => c.TrimStart().StartsWith("UPDATE sales.reservations", StringComparison.Ordinal)).ToList();
        Assert.Single(reservationUpdates);
        Assert.DoesNotContain(counter.Commands, c => c.TrimStart().StartsWith("SELECT", StringComparison.Ordinal));
        Assert.Equal(0, await ConcurrencyTests.CountActiveReservationsAsync(cs, EndpointTests.RiversidePlot1));
        Assert.All(await db.Units.AsNoTracking().ToListAsync(Ct), u => Assert.Equal(Domain.UnitStatus.Available, u.Status));
    }
}
