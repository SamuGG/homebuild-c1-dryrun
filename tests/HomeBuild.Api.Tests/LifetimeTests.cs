using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Tests;

public sealed class LifetimeTests(PostgresFixture database)
{
    private const string HillcrestPlot1 = "0198a000-0000-7000-8002-000000000001";
    private const string RiversidePlot1 = "0198a000-0000-7000-8001-000000000001";

    [Fact]
    public void Container_builds_with_scope_validation_on()
    {
        using var factory = Production("Host=unused");

        var services = factory.Services; // builds the container; ValidateOnBuild would throw here

        // ValidateScopes is on: a scoped service cannot come from the root provider.
        var ex = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IOptionsSnapshot<ReservationOptions>>());
        Assert.Contains("from root provider", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Singleton_depending_on_scoped_service_fails_at_startup()
    {
        using var factory = Production("Host=unused").WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<CapturesSnapshot>()));

        var ex = Assert.Throws<AggregateException>(() => factory.Services);

        Assert.Contains("Cannot consume scoped service", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_with_percentage_deposit_uses_percentage_strategy()
    {
        using var factory = Production(await database.CreateDatabaseAsync());
        using var client = factory.CreateClient();
        var deposit = EndpointTests.Deposit(5000); // above the fixed minimum (1000), below 5% of 330,000

        var percentage = await client.PostAsJsonAsync($"/v1/units/{HillcrestPlot1}/reservations", deposit, TestContext.Current.CancellationToken);
        var fixedRule = await client.PostAsJsonAsync($"/v1/units/{RiversidePlot1}/reservations", deposit, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, percentage.StatusCode);
        Assert.Contains("16500", await percentage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, fixedRule.StatusCode);
    }

    private static WebApplicationFactory<Program> Production(string connectionString) =>
        new ApiFactory(connectionString).WithWebHostBuilder(b => b.UseEnvironment("Production"));

    private sealed class CapturesSnapshot(IOptionsSnapshot<ReservationOptions> snapshot)
    {
        public ReservationOptions Value => snapshot.Value;
    }
}
