using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Accounts;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stripe;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C8 Lab 5's scenarios as integration tests: racing buyers, late payments, concurrent webhooks. Stripe is a stub that
/// records refunds; webhooks are signed with a secret generated per run.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class RaceTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly UnitId Plot1 = new(Guid.Parse(EndpointTests.RiversidePlot1));

    // A: 20 buyers reserve one unit at once (two API instances: each one's rate limiter allows 10 a minute per client).
    [Fact]
    public async Task A_twenty_buyers_one_unit_one_held_nineteen_conflicts()
    {
        var cs = await database.CreateDatabaseAsync();
        var buyers = Enumerable.Range(1, 20).Select(i => Guid.Parse($"0198a000-0000-7000-9000-0000000a{i:D4}")).ToList();
        await using (var accounts = AccountsModule.CreateDbContext(cs)) // the owner: the app role may not write memberships
        {
            accounts.TenantMemberships.AddRange(buyers.Select(b => new TenantMembership(b.ToString(), TestTenants.Demo, ["buyer"])));
            await accounts.SaveChangesAsync(Ct);
        }

        await using var instanceA = new ApiFactory(cs);
        await using var instanceB = new ApiFactory(cs);
        using var clientA = instanceA.CreateClient();
        using var clientB = instanceB.CreateClient();
        using var start = new SemaphoreSlim(0);
        var requests = buyers.Select(async (buyer, i) =>
        {
            await start.WaitAsync(Ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{Plot1.Value}/reservations") { Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(buyer.ToString(), "buyer"));
            using var response = await (i % 2 == 0 ? clientA : clientB).SendAsync(request, Ct);
            return response.StatusCode;
        }).ToList();
        start.Release(20);
        var statuses = await Task.WhenAll(requests);

        var summary = string.Join(", ", statuses.GroupBy(s => s).OrderBy(g => g.Key).Select(g => $"{(int)g.Key}×{g.Count()}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"A: {summary}");
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(19, statuses.Count(s => s == HttpStatusCode.Conflict));
    }

    // B: a buyer pays after their reservation expired and another buyer has reserved the unit.
    [Fact]
    public async Task B_late_payment_for_a_unit_another_buyer_holds_is_refunded_and_explained()
    {
        var stripe = new RefundStub();
        await using var api = await database.CreateApiAsync();
        await using var factory = With(api, stripe);
        using var client = factory.CreateClient();
        var late = await ReserveAsync(client, TestTokens.Users.Owner);
        await ExpireAsync(api, late);
        var other = await ReserveAsync(client, TestTokens.Users.OtherBuyer);

        using var webhook = await DeliverAsync(client, StripeTestEvents.Completed("evt_b_1", "cs_b_1", late));

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.IsType<Expired>(await StateAsync(api, late)); // never confirmed
        Assert.IsType<Held>(await StateAsync(api, other)); // the other buyer keeps the unit
        var refund = Assert.Single(stripe.Refunds);
        Assert.Equal(("pi_cs_b_1", "500000", "refund:cs_b_1"), (refund.PaymentIntent, refund.Amount, refund.IdempotencyKey));
        using var mine = new HttpRequestMessage(HttpMethod.Get, "/v2/me/deposits");
        mine.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer"));
        using var deposits = await client.SendAsync(mine, Ct);
        Assert.Equal(HttpStatusCode.OK, deposits.StatusCode);
        var deposit = Assert.Single(JsonElement.Parse(await deposits.Content.ReadAsStringAsync(Ct)).EnumerateArray());
        Assert.Equal(("refunded", late.Value), (deposit.GetProperty("status").GetString(), deposit.GetProperty("reservationId").GetGuid()));
        Assert.Contains("another buyer", deposit.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    // C: a buyer pays after their reservation expired, and the unit is still free.
    [Fact]
    public async Task C_late_payment_for_a_free_unit_reholds_and_confirms()
    {
        var stripe = new RefundStub();
        await using var api = await database.CreateApiAsync();
        await using var factory = With(api, stripe);
        using var client = factory.CreateClient();
        var late = await ReserveAsync(client, TestTokens.Users.Owner);
        await ExpireAsync(api, late);

        using var webhook = await DeliverAsync(client, StripeTestEvents.Completed("evt_c_1", "cs_c_1", late));

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.IsType<Confirmed>(await StateAsync(api, late));
        await using var sales = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.Equal(UnitStatus.Reserved, (await sales.Units.AsNoTracking().SingleAsync(u => u.Id == Plot1, Ct)).Status);
        Assert.Empty(stripe.Refunds);
    }

    // D: two deliveries of the same event at the same moment on two API instances.
    [Fact]
    public async Task D_same_webhook_on_two_instances_records_one_deposit()
    {
        var stripe = new RefundStub();
        await using var api = await database.CreateApiAsync();
        await using var instanceA = With(api, stripe);
        await using var instanceB = With(api, stripe);
        using var clientA = instanceA.CreateClient();
        using var clientB = instanceB.CreateClient();
        var reservation = await ReserveAsync(clientA, TestTokens.Users.Owner);
        var payload = StripeTestEvents.Completed("evt_d_1", "cs_d_1", reservation);

        var responses = await Task.WhenAll(DeliverAsync(clientA, payload), DeliverAsync(clientB, payload));

        await using var payments = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var deposits = await payments.Deposits.CountAsync(Ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"D: {string.Join(", ", responses.Select(r => (int)r.StatusCode))}; deposits: {deposits}");
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, deposits);
        Assert.IsType<Confirmed>(await StateAsync(api, reservation));
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }

    // The refund is called after the commit. If that call fails, the intent is already stored and the event recorded;
    // Stripe's redelivery comes in as a duplicate and finishes the refund (with the same idempotency key).
    [Fact]
    public async Task Failed_refund_call_is_finished_by_the_redelivery()
    {
        var stripe = new RefundStub { FailFirst = true };
        await using var api = await database.CreateApiAsync();
        await using var factory = With(api, stripe);
        using var client = factory.CreateClient();
        var late = await ReserveAsync(client, TestTokens.Users.Owner);
        await ExpireAsync(api, late);
        await ReserveAsync(client, TestTokens.Users.OtherBuyer);
        var payload = StripeTestEvents.Completed("evt_retry_1", "cs_retry_1", late);

        using var first = await DeliverAsync(client, payload);
        using var redelivered = await DeliverAsync(client, payload);

        Assert.Equal((HttpStatusCode.InternalServerError, HttpStatusCode.OK), (first.StatusCode, redelivered.StatusCode));
        Assert.Equal(2, stripe.Attempts); // the same key both times: Stripe refunds once
        await using var payments = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var deposit = await payments.Deposits.SingleAsync(Ct);
        Assert.Equal("re_stub_pi_cs_retry_1", deposit.RefundReference);
    }

    // Paying twice (two Checkout pages for one reservation, Lab 1): the second payment is given back.
    [Fact]
    public async Task Second_payment_for_a_confirmed_reservation_is_refunded()
    {
        var stripe = new RefundStub();
        await using var api = await database.CreateApiAsync();
        await using var factory = With(api, stripe);
        using var client = factory.CreateClient();
        var reservation = await ReserveAsync(client, TestTokens.Users.Owner);

        using var paid = await DeliverAsync(client, StripeTestEvents.Completed("evt_twice_1", "cs_twice_1", reservation));
        using var again = await DeliverAsync(client, StripeTestEvents.Completed("evt_twice_2", "cs_twice_2", reservation));

        Assert.IsType<Confirmed>(await StateAsync(api, reservation));
        var refund = Assert.Single(stripe.Refunds);
        Assert.Equal("pi_cs_twice_2", refund.PaymentIntent);
    }

    private static WebApplicationFactory<Program> With(ApiFactory api, RefundStub stripe) =>
        api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Payments:WebhookSecret", StripeTestEvents.Secret);
            builder.ConfigureTestServices(services => services.AddSingleton<IStripeClient>(
                new StripeClient("stub-key-not-a-secret", httpClient: new SystemNetHttpClient(new HttpClient(stripe), maxNetworkRetries: 0))));
        });

    private static async Task<ReservationId> ReserveAsync(HttpClient client, Guid buyer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{Plot1.Value}/reservations") { Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(buyer.ToString(), "buyer"));
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"reserve as {buyer}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return new ReservationId(JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)).GetProperty("id").GetGuid());
    }

    /// <summary>Moves the hold's end into the past and runs Sales' expiry, which releases the unit.</summary>
    private static async Task ExpireAsync(ApiFactory api, ReservationId id)
    {
        await using (var owner = new Npgsql.NpgsqlConnection(api.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            var ended = DateTimeOffset.UtcNow.AddMinutes(-1);
            await using var past = new Npgsql.NpgsqlCommand("UPDATE sales.reservations SET expires_at = @ended, state = @state WHERE id = @id", owner);
            past.Parameters.AddWithValue("ended", ended);
            past.Parameters.AddWithValue("state", "Held|" + ended.ToString("O", System.Globalization.CultureInfo.InvariantCulture)); // as ReservationStateConverter stores it
            past.Parameters.AddWithValue("id", id.Value);
            Assert.Equal(1, await past.ExecuteNonQueryAsync(Ct));
        }

        await using var scope = api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestTenantContext>().TenantId = TestTenants.Demo;
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ReservationExpiry>().ExpireStaleAsync(DateTimeOffset.UtcNow, Ct));
    }

    private static async Task<HttpResponseMessage> DeliverAsync(HttpClient client, string payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", StripeTestEvents.Sign(payload, StripeTestEvents.Secret, DateTimeOffset.UtcNow));
        request.Headers.Authorization = null;
        return await client.SendAsync(request, Ct);
    }

    private static async Task<ReservationState> StateAsync(ApiFactory api, ReservationId id)
    {
        await using var sales = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        return (await sales.Reservations.AsNoTracking().SingleAsync(r => r.Id == id, Ct)).State;
    }

    /// <summary>Records each refund HomeBuild asks Stripe for, and answers like Stripe.</summary>
    private sealed class RefundStub : HttpMessageHandler
    {
        public List<(string PaymentIntent, string Amount, string IdempotencyKey)> Refunds { get; } = [];

        public bool FailFirst { get; init; }

        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/refunds", request.RequestUri!.AbsolutePath);
            var form = QueryHelpers.ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken));
            if (++Attempts == 1 && FailFirst)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"type\":\"api_error\",\"message\":\"stub outage\"}}", Encoding.UTF8, "application/json") };
            }

            lock (Refunds)
            {
                Refunds.Add((form["payment_intent"]!, form["amount"]!, request.Headers.GetValues("Idempotency-Key").Single()));
            }

            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = "re_stub_" + form["payment_intent"], ["object"] = "refund", ["status"] = "succeeded",
                ["amount"] = long.Parse(form["amount"]!, System.Globalization.CultureInfo.InvariantCulture), ["currency"] = "gbp",
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
