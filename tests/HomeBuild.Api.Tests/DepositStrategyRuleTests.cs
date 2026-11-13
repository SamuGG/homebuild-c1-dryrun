using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using static HomeBuild.Testing.Builders;

namespace HomeBuild.Api.Tests;

/// <summary>The minimum deposit per strategy, and the deposit currency rule at the API (docs/testing.md).</summary>
public sealed class DepositStrategyRuleTests(PostgresFixture database)
{
    public static TheoryData<string, decimal, decimal> Minimums => new()
    {
        new TheoryDataRow<string, decimal, decimal>("fixed", 250_000m, 1_000m).WithTestDisplayName("Minimum deposit: fixed is the policy minimum whatever the price"),
        new TheoryDataRow<string, decimal, decimal>("fixed", 500m, 1_000m).WithTestDisplayName("Minimum deposit: fixed stays the policy minimum above a low price"),
        new TheoryDataRow<string, decimal, decimal>("percentage", 330_000m, 16_500m).WithTestDisplayName("Minimum deposit: percentage is 5% of the price"),
        new TheoryDataRow<string, decimal, decimal>("percentage", 250_000.10m, 12_500.00m).WithTestDisplayName("Minimum deposit: percentage rounds half a penny to even (12,500.005 → 12,500.00)"),
        new TheoryDataRow<string, decimal, decimal>("percentage", 250_000.30m, 12_500.02m).WithTestDisplayName("Minimum deposit: percentage rounds half a penny to even (12,500.015 → 12,500.02)"),
    };

    [Theory]
    [MemberData(nameof(Minimums))]
    public void Minimum_deposit_per_strategy(string strategy, decimal price, decimal minimum)
    {
        var unit = AUnit().PricedAt(Amount(price)).Build();

        var policy = new DepositStrategySelector(new FixedDepositStrategy(), new PercentageDepositStrategy()).PolicyFor(unit, strategy, APolicy().Build());

        Assert.Equal(Amount(minimum), policy.MinimumDeposit);
    }

    [Fact]
    public async Task Deposit_in_another_currency_is_422_not_500()
    {
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/v1/units/{EndpointTests.RiversidePlot1}/reservations",
            new { deposit = 5000, currency = "EUR" }, TestContext.Current.CancellationToken);
        var problem = JsonElement.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("Deposit currency rejected", problem.GetProperty("title").GetString());
        Assert.Equal("The deposit must be in GBP, the unit's currency.", problem.GetProperty("detail").GetString());
        Assert.Equal(422, problem.GetProperty("status").GetInt32());
    }
}
