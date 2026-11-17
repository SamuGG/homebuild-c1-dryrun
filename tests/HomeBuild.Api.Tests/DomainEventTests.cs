using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Modules.Extras;
using HomeBuild.Api.Modules.Notifications;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

/// <summary>C7 Lab 5: MilestoneCompleted, dispatched after commit to Payments, Extras and Notifications.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class DomainEventTests(PostgresFixture database) : IAsyncLifetime
{
    // Riverside ("fixed" stages: £10,000 at foundations, £15,000 at roof). Plot 2 has foundations and frame done, roof in progress.
    private static readonly UnitId RiversidePlot2 = new(Guid.Parse("0198a000-0000-7000-8001-000000000002"));
    private static readonly DateOnly Today = new(2026, 11, 17);

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await database.CreateApiAsync();
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Completing_milestone_creates_stage_payment_due()
    {
        using var response = await CompleteAsync(RiversidePlot2, "Roof");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var due = Assert.Single(await StagePaymentsAsync(RiversidePlot2));
        Assert.Equal((Milestone.Roof, 15_000m, "GBP", Today), (due.Milestone, due.Amount, due.Currency, due.DueFrom));
    }

    [Fact]
    public async Task Event_not_dispatched_when_transaction_rolls_back()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<RequestTenantContext>().TenantId = TestTenants.Demo;
            var db = scope.ServiceProvider.GetRequiredService<ConstructionDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var progress = await db.BuildProgress.SingleAsync(p => p.UnitId == RiversidePlot2, Ct);
            Assert.IsType<MilestoneDone>(progress.Complete(Milestone.Roof, "site-manager", Today).Value);
            await db.SaveChangesAsync(Ct);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Empty(await StagePaymentsAsync(RiversidePlot2));
        await using var check = ConstructionModule.CreateDbContext(_factory.ConnectionString, TestTenants.Demo);
        Assert.Equal(MilestoneStatus.InProgress, (await check.BuildProgress.AsNoTracking().SingleAsync(p => p.UnitId == RiversidePlot2, Ct))[Milestone.Roof].Status);
    }

    [Fact]
    public async Task Event_is_dispatched_after_an_explicit_transaction_commits()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<RequestTenantContext>().TenantId = TestTenants.Demo;
            var db = scope.ServiceProvider.GetRequiredService<ConstructionDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var progress = await db.BuildProgress.SingleAsync(p => p.UnitId == RiversidePlot2, Ct);
            progress.Complete(Milestone.Roof, "site-manager", Today);
            await db.SaveChangesAsync(Ct);
            Assert.Empty(await StagePaymentsAsync(RiversidePlot2)); // saved, not yet committed: nothing dispatched
            await transaction.CommitAsync(Ct);
        }

        Assert.Single(await StagePaymentsAsync(RiversidePlot2));
    }

    [Fact]
    public async Task Handler_is_idempotent()
    {
        var milestoneCompleted = new MilestoneCompleted(RiversidePlot2, Milestone.Frame, Today);
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestTenantContext>().TenantId = TestTenants.Demo;
        // Reserve the unit so Notifications has a buyer to write to.
        using (var reserve = new HttpRequestMessage(HttpMethod.Post, $"/v2/units/{RiversidePlot2.Value}/reservations"))
        {
            reserve.Content = JsonContent.Create(new { deposit = 5000, currency = "GBP" });
            Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(reserve, Ct)).StatusCode);
        }

        var roof = new MilestoneCompleted(RiversidePlot2, Milestone.Roof, Today);
        var handlers = scope.ServiceProvider.GetServices<IHandleDomainEvent<MilestoneCompleted>>().ToList();
        Assert.Equal(3, handlers.Count); // Payments, Extras, Notifications
        foreach (var handler in handlers)
        {
            foreach (var e in new[] { milestoneCompleted, roof, milestoneCompleted, roof })
            {
                await handler.HandleAsync(e, Ct);
            }
        }

        Assert.Single(await StagePaymentsAsync(RiversidePlot2)); // roof; frame has no stage in a fixed schedule
        Assert.NotNull(_factory.Services.GetRequiredService<ExtrasLedger>().For(RiversidePlot2));
        Assert.Equal(2, _factory.Services.GetRequiredService<BuyerMessages>().For(RiversidePlot2).Count); // frame and roof, once each
    }

    [Fact]
    public async Task Buyer_view_has_no_internal_fields()
    {
        // The owner reserves plot 2; the site manager completes its roof. Both views update; the buyer's holds no staff data.
        var owner = TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer");
        using (var reserve = new HttpRequestMessage(HttpMethod.Post, $"/v2/units/{RiversidePlot2.Value}/reservations"))
        {
            reserve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);
            reserve.Content = JsonContent.Create(new { deposit = 5000, currency = "GBP" });
            Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(reserve, Ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(RiversidePlot2, "Roof")).StatusCode);

        using var buyerRequest = new HttpRequestMessage(HttpMethod.Get, "/v2/me/progress");
        buyerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner);
        var buyerBody = await (await _client.SendAsync(buyerRequest, Ct)).Content.ReadAsStringAsync(Ct);
        using var staffRequest = new HttpRequestMessage(HttpMethod.Get, "/v2/developments/0198a000-0000-7000-8000-000000000001/board");
        staffRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager"));
        var staff = JsonElement.Parse(await (await _client.SendAsync(staffRequest, Ct)).Content.ReadAsStringAsync(Ct));

        var progress = JsonElement.Parse(buyerBody).GetProperty("homes")[0].GetProperty("progress");
        Assert.Equal(("First fix", 3), (progress.GetProperty("stage").GetString(), progress.GetProperty("stepsDone").GetInt32()));
        var buyerFields = PropertyNames(JsonElement.Parse(buyerBody)).ToHashSet();
        Assert.Subset(
            new HashSet<string> { "homes", "reservationId", "unitId", "plotNumber", "developmentName", "reservationState", "reservationExpiresAt", "progress", "stage", "stepsDone", "stepsTotal", "nextStep" },
            buyerFields);
        foreach (var internalValue in new[] { TestTokens.Users.Staff("site-manager").ToString(), "completedBy", "Roofing", "subcontractor", "siteNotes", "openDefects", "15000" })
        {
            Assert.DoesNotContain(internalValue, buyerBody, StringComparison.OrdinalIgnoreCase);
        }

        var roofCell = staff.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("plotNumber").GetString() == "P2")
            .GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("milestone").GetString() == "Roof");
        Assert.Equal(("Done", "2026-11-17"), (roofCell.GetProperty("status").GetString(), roofCell.GetProperty("completedOn").GetString()));
        var message = Assert.Single(_factory.Services.GetRequiredService<BuyerMessages>().For(RiversidePlot2));
        Assert.DoesNotContain("Roofing", message.Text, StringComparison.Ordinal); // the buyer's message: no subcontractor either
    }

    [Fact]
    public async Task Failing_handler_does_not_fail_the_request_or_stop_the_others()
    {
        // A handler registered first that always throws (the Lab 5 Break, kept as a test).
        await using var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Insert(0, ServiceDescriptor.Scoped<IHandleDomainEvent<MilestoneCompleted>, ThrowingHandler>())));
        using var client = host.CreateClient();
        using (var reserve = new HttpRequestMessage(HttpMethod.Post, $"/v2/units/{RiversidePlot2.Value}/reservations"))
        {
            reserve.Content = JsonContent.Create(new { deposit = 5000, currency = "GBP" });
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(reserve, Ct)).StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v2/units/{RiversidePlot2.Value}/milestones/Roof/completion");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager"));
        request.Content = JsonContent.Create(new { completedOn = Today });
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the milestone is committed whatever a handler does
        Assert.Single(await StagePaymentsAsync(RiversidePlot2));
        Assert.Single(host.Services.GetRequiredService<BuyerMessages>().For(RiversidePlot2));
    }

    private sealed class ThrowingHandler : IHandleDomainEvent<MilestoneCompleted>
    {
        public Task HandleAsync(MilestoneCompleted domainEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A handler that always fails.");
    }

    private async Task<HttpResponseMessage> CompleteAsync(UnitId unit, string milestone)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v2/units/{unit.Value}/milestones/{milestone}/completion");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager"));
        request.Content = JsonContent.Create(new { completedOn = Today });
        return await _client.SendAsync(request, Ct);
    }

    private async Task<List<StagePaymentRow>> StagePaymentsAsync(UnitId unit)
    {
        await using var db = PaymentsModule.CreateDbContext(_factory.ConnectionString, TestTenants.Demo);
        return await db.StagePayments.AsNoTracking().Where(p => p.UnitId == unit).ToListAsync(Ct);
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };
}
