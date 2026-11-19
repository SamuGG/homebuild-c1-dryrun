using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeBuild.Api.Data;
using HomeBuild.Api.Modules.Extras;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stripe;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 2's acceptance tests: the extras basket, its price snapshots and revalidation at payment. Stripe is a stub
/// that records Checkout sessions and refunds; webhooks are signed with the run's secret.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class ExtrasBasketTests(PostgresFixture database)
{
    private static readonly Guid Unit = Guid.Parse(EndpointTests.RiversidePlot1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Price_change_after_adding_does_not_change_order_total()
    {
        var (api, factory, stripe, client) = await StartAsync();
        await using var _ = api;
        await using var __ = factory;
        var kitchen = await ExtraIdAsync(api, "Premium kitchen");
        await AddAsync(client, kitchen);
        await SetPriceAsync(api, "Premium kitchen", 7_000m); // the catalogue changes after the buyer added it

        var sessionId = await CheckoutAsync(client);
        Assert.Equal("650000", stripe.Sessions.Single()["line_items[0][price_data][unit_amount]"]); // the snapshot, in pence
        await PayAsync(client, sessionId, amountTotal: 650_000);

        var basket = await BasketAsync(api);
        Assert.Equal(BasketStatus.Paid, basket.Status);
        Assert.Equal(6_500m, basket.ChargedAmount);
        Assert.Equal(6_500m, Assert.Single(basket.Lines).Amount);
        Assert.Equal(6_500m, Assert.Single(await PaidMessagesAsync(api)).Total);
    }

    // Solar closes at Roof. The buyer opens the Checkout page; Roof completes; then the payment arrives: the kitchen is
    // kept, the solar line refunded (its snapshot amount), and ExtrasPaid names only the kitchen.
    [Fact]
    public async Task Item_closed_before_payment_is_refunded()
    {
        var (api, factory, stripe, client) = await StartAsync();
        await using var _ = api;
        await using var __ = factory;
        await AddAsync(client, await ExtraIdAsync(api, "Premium kitchen"));
        await AddAsync(client, await ExtraIdAsync(api, "Solar panels"));
        var sessionId = await CheckoutAsync(client);
        Assert.Equal("2", stripe.Sessions.Single().Keys.Count(k => k.EndsWith("[quantity]", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture));

        await ExtrasTestEvents.DeliverMilestoneAsync(factory, Unit, Milestone.Roof, version: 3);
        await PayAsync(client, sessionId, amountTotal: 1_230_000);

        var basket = await BasketAsync(api);
        var solarLine = basket.Lines.Single(l => l.Name == "Solar panels");
        Assert.Equal(BasketLineStatus.Refunded, solarLine.Status);
        Assert.StartsWith("Solar panels closed when Roof was completed.", solarLine.RefundReason, StringComparison.Ordinal);
        Assert.Equal(BasketLineStatus.Paid, basket.Lines.Single(l => l.Name == "Premium kitchen").Status);
        var refund = Assert.Single(stripe.Refunds);
        Assert.Equal(("pi_" + sessionId, "580000", $"extras-refund:{solarLine.Id}"), refund);
        var paid = Assert.Single(await PaidMessagesAsync(api));
        Assert.Equal(["Premium kitchen"], paid.Extras.Select(e => e.Name));
        Assert.Equal(6_500m, paid.Total);
    }

    // At add: the two kitchens exclude each other. At payment: a kitchen bought meanwhile through another basket for the
    // same home (two tabs, or a sales agent) makes the second kitchen conflict, and it is refunded.
    [Fact]
    public async Task Conflicting_extras_rejected_at_add_and_at_payment()
    {
        var (api, factory, stripe, client) = await StartAsync();
        await using var _ = api;
        await using var __ = factory;
        await AddAsync(client, await ExtraIdAsync(api, "Premium kitchen"));

        using var rejected = await AddResponseAsync(client, await ExtraIdAsync(api, "Designer kitchen"));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("Designer kitchen cannot be combined with Premium kitchen.", await rejected.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var sessionId = await CheckoutAsync(client);
        await InsertPaidBasketAsync(api, await ExtraIdAsync(api, "Designer kitchen"), "Designer kitchen", 11_000m);
        await PayAsync(client, sessionId, amountTotal: 650_000);

        var line = Assert.Single((await BasketAsync(api)).Lines);
        Assert.Equal(BasketLineStatus.Refunded, line.Status);
        Assert.StartsWith("Premium kitchen cannot be combined with Designer kitchen.", line.RefundReason, StringComparison.Ordinal);
        Assert.Single(stripe.Refunds);
        Assert.Empty(await PaidMessagesAsync(api)); // nothing kept, nothing to order
    }

    // Stripe redelivers the event (same ID), and the dashboard's "resend" sends it again: one order, one ExtrasPaid.
    [Fact]
    public async Task Duplicate_payment_webhook_records_one_order()
    {
        var (api, factory, stripe, client) = await StartAsync();
        await using var _ = api;
        await using var __ = factory;
        await AddAsync(client, await ExtraIdAsync(api, "Smart home package"));
        var sessionId = await CheckoutAsync(client);

        await PayAsync(client, sessionId, amountTotal: 240_000, eventId: "evt_dup_1");
        await PayAsync(client, sessionId, amountTotal: 240_000, eventId: "evt_dup_1");
        await PayAsync(client, sessionId, amountTotal: 240_000, eventId: "evt_dup_2");

        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.Equal(1, await db.Baskets.CountAsync(b => b.Status == BasketStatus.Paid, Ct));
        Assert.Single(await PaidMessagesAsync(api));
        Assert.Empty(stripe.Refunds);
    }

    [Fact]
    public async Task My_baskets_show_confirming_then_paid()
    {
        var (api, factory, _, client) = await StartAsync();
        await using var __ = api;
        await using var ___ = factory;
        await AddAsync(client, await ExtraIdAsync(api, "Premium kitchen"));
        var sessionId = await CheckoutAsync(client);

        Assert.Equal("confirming", (await MyBasketsAsync(client))[0].GetProperty("status").GetString());
        await PayAsync(client, sessionId, amountTotal: 650_000);
        Assert.Equal("paid", (await MyBasketsAsync(client))[0].GetProperty("status").GetString());
    }

    /// <summary>The owner holds Riverside plot 1, whose Frame is done (Extras has been told): kitchens, smart home, solar and parking are open.</summary>
    private async Task<(ApiFactory Api, WebApplicationFactory<Program> Factory, ExtrasStripeStub Stripe, HttpClient Client)> StartAsync()
    {
        var api = await database.CreateApiAsync();
        var stripe = new ExtrasStripeStub();
        var factory = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Payments:WebhookSecret", StripeTestEvents.Secret);
            builder.UseSetting("Payments:WebBaseUrl", "http://localhost:5266/");
            builder.ConfigureTestServices(services => services.AddSingleton<IStripeClient>(
                new StripeClient("stub-key-not-a-secret", httpClient: new SystemNetHttpClient(new HttpClient(stripe), maxNetworkRetries: 0))));
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer"));
        using (var reserve = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{Unit}/reservations") { Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }) })
        {
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(reserve, Ct)).StatusCode);
        }

        await ExtrasTestEvents.DeliverMilestoneAsync(factory, Unit, Milestone.Frame, version: 2);
        return (api, factory, stripe, client);
    }

    private static async Task<Guid> ExtraIdAsync(ApiFactory api, string name)
    {
        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        return (await db.Extras.AsNoTracking().SingleAsync(e => e.Name == name && e.DevelopmentId == SeedData.Riverside, Ct)).Id;
    }

    private static async Task SetPriceAsync(ApiFactory api, string name, decimal price)
    {
        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        await db.Extras.Where(e => e.Name == name).ExecuteUpdateAsync(set => set.SetProperty(e => e.Price, price), Ct);
    }

    private static async Task<HttpResponseMessage> AddResponseAsync(HttpClient client, Guid extraId) =>
        await client.PostAsJsonAsync("/v2/me/basket/lines", new { unitId = Unit, extraId, quantity = 1 }, Ct);

    private static async Task<Guid> AddAsync(HttpClient client, Guid extraId)
    {
        using var response = await AddResponseAsync(client, extraId);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"add: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return extraId;
    }

    private static async Task<string> CheckoutAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v2/me/basket/checkout") { Content = JsonContent.Create(new { unitId = Unit }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"checkout: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)).GetProperty("sessionId").GetString()!;
    }

    private static async Task<List<JsonElement>> MyBasketsAsync(HttpClient client) =>
        [.. JsonElement.Parse(await client.GetStringAsync(new Uri("/v2/me/baskets", UriKind.Relative), Ct)).EnumerateArray()];

    /// <summary>Stripe's checkout.session.completed for the basket's session, signed.</summary>
    private static async Task PayAsync(HttpClient client, string sessionId, long amountTotal, string? eventId = null)
    {
        var basketId = ExtrasStripeStub.BasketOf(sessionId);
        var payload = ExtrasTestEvents.Completed(eventId ?? "evt_" + Guid.NewGuid().ToString("N"), sessionId, basketId, Unit, amountTotal);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", StripeTestEvents.Sign(payload, StripeTestEvents.Secret, DateTimeOffset.UtcNow));
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<BasketRow> BasketAsync(ApiFactory api)
    {
        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        return await db.Baskets.AsNoTracking().Include(b => b.Lines).Where(b => b.SessionId != null && b.SessionId.StartsWith("cs_test_stub")).SingleAsync(Ct);
    }

    private static async Task<List<ExtrasPaidMessage>> PaidMessagesAsync(ApiFactory api)
    {
        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        return [.. (await db.Set<OutboxMessage>().AsNoTracking().Where(m => m.Type == IntegrationEvents.ExtrasPaid).ToListAsync(Ct))
            .Select(m => JsonSerializer.Deserialize<ExtrasPaidMessage>(m.Payload, JsonSerializerOptions.Web)!)];
    }

    /// <summary>Another basket for the same home, already paid (a second tab, or a sales agent's order).</summary>
    private static async Task InsertPaidBasketAsync(ApiFactory api, Guid extraId, string name, decimal price)
    {
        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        db.Baskets.Add(new BasketRow
        {
            BuyerId = new BuyerId(TestTokens.Users.Owner), UnitId = new UnitId(Unit), DevelopmentId = SeedData.Riverside, CreatedAt = DateTimeOffset.UtcNow,
            Status = BasketStatus.Paid, SessionId = "cs_other_tab", PaymentIntent = "pi_other_tab", ChargedAmount = price, PaidAt = DateTimeOffset.UtcNow,
            Lines = [new BasketLineRow { ExtraId = extraId, Name = name, UnitPrice = price, Currency = "GBP", AddedAt = DateTimeOffset.UtcNow, Status = BasketLineStatus.Paid }],
        });
        await db.SaveChangesAsync(Ct);
    }
}

/// <summary>Stands in for api.stripe.com: Checkout Session creates (with their line items) and refunds.</summary>
internal sealed class ExtrasStripeStub : HttpMessageHandler
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Guid> Baskets = new();

    public List<Dictionary<string, string>> Sessions { get; } = [];

    public List<(string PaymentIntent, string Amount, string IdempotencyKey)> Refunds { get; } = [];

    public static Guid BasketOf(string sessionId) => Baskets[sessionId];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = QueryHelpers.ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken)).ToDictionary(f => f.Key, f => f.Value.ToString());
        object body;
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/v1/checkout/sessions":
                lock (Sessions)
                {
                    Sessions.Add(form);
                }

                var id = "cs_test_stub_" + Guid.NewGuid().ToString("N");
                Baskets[id] = Guid.Parse(form["client_reference_id"]);
                body = new Dictionary<string, object?>
                {
                    ["id"] = id, ["object"] = "checkout.session", ["url"] = "https://checkout.stripe.com/c/pay/" + id,
                    ["expires_at"] = long.Parse(form["expires_at"], CultureInfo.InvariantCulture), ["client_reference_id"] = form["client_reference_id"], ["livemode"] = false,
                };
                break;
            case "/v1/refunds":
                lock (Refunds)
                {
                    Refunds.Add((form["payment_intent"], form["amount"], request.Headers.GetValues("Idempotency-Key").Single()));
                }

                body = new Dictionary<string, object?> { ["id"] = "re_stub_" + Refunds.Count, ["object"] = "refund", ["status"] = "succeeded", ["amount"] = long.Parse(form["amount"], CultureInfo.InvariantCulture), ["currency"] = "gbp" };
                break;
            default:
                throw new InvalidOperationException($"Unexpected Stripe call {request.Method} {request.RequestUri.AbsolutePath}");
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}

/// <summary>Extras' test events: a milestone as homebuild-extras delivers it, and Stripe's completed event for a basket.</summary>
internal static class ExtrasTestEvents
{
    public static async Task DeliverMilestoneAsync(WebApplicationFactory<Program> api, Guid unit, Milestone milestone, int version)
    {
        var body = JsonSerializer.Serialize(new MilestoneCompletedMessage(unit, TestTenants.Demo, milestone.ToString(), new DateOnly(2026, 11, 19), version, SeedData.Riverside.Value), JsonSerializerOptions.Web);
        var message = new QueueMessage(Guid.NewGuid().ToString(), "receipt", body,
            new Dictionary<string, string> { ["type"] = IntegrationEvents.MilestoneCompleted, ["message_id"] = Guid.NewGuid().ToString() }, 1);
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredKeyedService<IMessageHandler>(ExtrasProgressConsumer.Name).HandleAsync(message, TestContext.Current.CancellationToken);
    }

    public static string Completed(string eventId, string sessionId, Guid basketId, Guid unit, long amountTotal) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = eventId, ["object"] = "event", ["api_version"] = StripeConfiguration.ApiVersion, ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["livemode"] = false, ["type"] = "checkout.session.completed",
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = new Dictionary<string, object?>
                {
                    ["id"] = sessionId, ["object"] = "checkout.session", ["client_reference_id"] = basketId.ToString(),
                    ["metadata"] = new Dictionary<string, string> { ["kind"] = "extras", ["tenant_id"] = TestTenants.Demo.ToString(), ["unit_id"] = unit.ToString(), ["basket_id"] = basketId.ToString() },
                    ["amount_total"] = amountTotal, ["currency"] = "gbp", ["payment_intent"] = "pi_" + sessionId,
                    ["status"] = "complete", ["payment_status"] = "paid", ["livemode"] = false,
                },
            },
        }, Indented);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
