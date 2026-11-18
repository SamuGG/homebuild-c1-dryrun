using System.Net;
using System.Text;
using System.Text.Json;
using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Domain;
using HomeBuild.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stripe;

namespace HomeBuild.Api.Tests;

/// <summary>C8 Lab 4: explicit transactions under the execution strategy, and no external call inside one.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class TransactionTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Documents the error: with the retrying strategy on, a transaction begun outside strategy.ExecuteAsync fails at its
    // first command, so a forgotten wrapper is found by the first test that runs it, not by the first transient fault.
    [Fact]
    public async Task User_transaction_without_strategy_fails_fast()
    {
        await using var api = await database.CreateApiAsync();
        await using var db = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);

        // BEGIN itself succeeds; the strategy refuses the first command that runs inside the user transaction.
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => db.StagePayments.CountAsync(Ct));

        TestContext.Current.TestOutputHelper?.WriteLine(e.Message);
        Assert.Contains("does not support user-initiated transactions", e.Message, StringComparison.Ordinal);
        Assert.Contains("CreateExecutionStrategy()", e.Message, StringComparison.Ordinal);
    }

    // The interceptor counts the request's open transactions; the Stripe adapter refuses to call out while one is open.
    [Fact]
    public async Task No_stripe_call_inside_transaction()
    {
        var stripe = new CountingStripe();
        await using var api = await database.CreateApiAsync();
        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestTenantContext>().TenantId = TestTenants.Demo;
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var gateway = new StripePaymentGateway(new StripeClient("stub-key-not-a-secret", httpClient: new SystemNetHttpClient(new HttpClient(stripe), maxNetworkRetries: 0)),
            scope.ServiceProvider.GetRequiredService<OpenTransactions>());
        var request = new CheckoutRequest(ReservationId.New(), TestTenants.Demo, UnitId.New(), new Money(5000m, "GBP"), "test",
            new Uri("http://web.test/ok"), new Uri("http://web.test/cancel"), DateTimeOffset.UtcNow.AddHours(2), "deposit-session:test");

        // Inside a transaction: refused before any HTTP call.
        var inside = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            return await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.CreateCheckoutSessionAsync(request, Ct));
        });
        Assert.Contains("inside a database transaction", inside.Message, StringComparison.Ordinal);
        Assert.Equal(0, stripe.Calls);

        // After the transaction ends (rolled back on dispose): allowed.
        await gateway.CreateCheckoutSessionAsync(request, Ct);
        Assert.Equal(1, stripe.Calls);
    }

    private sealed class CountingStripe : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = "cs_test_counted", ["object"] = "checkout.session", ["url"] = "https://checkout.stripe.com/c/pay/cs_test_counted",
                ["expires_at"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
