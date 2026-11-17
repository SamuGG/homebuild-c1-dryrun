using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Domain;

namespace HomeBuild.Api.Tests;

/// <summary>C7 Lab 6: the overdue list through the API and the EF Core adapter (the rule itself is tested in the core).</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class OverdueStagePaymentsApiTests(PostgresFixture database)
{
    [Theory]
    [InlineData("finance", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("buyer", HttpStatusCode.Forbidden)]
    [InlineData("site-manager", HttpStatusCode.Forbidden)]
    public async Task Overdue_stage_payments_are_listed_for_finance_through_the_adapter(string role, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = await database.CreateApiAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var overdue = UnitId.New();
        await using (var db = PaymentsModule.CreateDbContext(factory.ConnectionString, TestTenants.Demo))
        {
            db.StagePayments.AddRange(
                new StagePaymentRow { UnitId = overdue, Milestone = Milestone.Roof, Amount = 15_000m, Currency = "GBP", DueFrom = today.AddDays(-30) },
                new StagePaymentRow { UnitId = UnitId.New(), Milestone = Milestone.Roof, Amount = 15_000m, Currency = "GBP", DueFrom = today.AddDays(-3) },
                new StagePaymentRow { UnitId = UnitId.New(), Milestone = Milestone.Roof, Amount = 15_000m, Currency = "GBP", DueFrom = today.AddDays(-30), PaidAt = DateTimeOffset.UtcNow, ProviderReference = "ch_1" });
            await db.SaveChangesAsync(ct);
        }

        using var client = factory.CreateClient();
        var subject = role == "buyer" ? TestTokens.Users.Owner : TestTokens.Users.Staff(role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(subject.ToString(), role));
        using var response = await client.GetAsync("/v2/stage-payments/overdue", ct);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var item = Assert.Single(JsonElement.Parse(await response.Content.ReadAsStringAsync(ct)).EnumerateArray());
            Assert.Equal((overdue.Value, 16), (item.GetProperty("unitId").GetGuid(), item.GetProperty("daysOverdue").GetInt32()));
        }
    }
}
