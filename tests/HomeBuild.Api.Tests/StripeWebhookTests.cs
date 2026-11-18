using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Logging;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C8 Lab 2's acceptance tests. Events are built and signed here, the way Stripe signs them (HMAC-SHA256 over
/// "{timestamp}.{raw body}"), with a secret generated per test run: CI never has a Stripe secret.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class StripeWebhookTests(PostgresFixture database)
{
    private const string Path = "/webhooks/stripe";

    // Generated at run time; Stripe's secrets have this prefix, and the value is any string.
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Tamperings => ["wrong-secret", "tampered-body", "missing-header", "stale-timestamp"];

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task Invalid_signature_returns_400(string tampering)
    {
        var (factory, api) = await CreateApiAsync();
        await using var _ = factory;
        using var client = Anonymous(factory);
        var reservation = await ReserveAsync(factory);
        var payload = Completed("evt_bad_1", "cs_bad_1", reservation);
        var signedAt = tampering == "stale-timestamp" ? DateTimeOffset.UtcNow.AddMinutes(-10) : DateTimeOffset.UtcNow;
        var header = Sign(payload, tampering == "wrong-secret" ? Secret + "x" : Secret, signedAt);
        var body = tampering == "tampered-body" ? payload.Replace("500000", "1", StringComparison.Ordinal) : payload;

        using var response = await PostAsync(client, body, tampering == "missing-header" ? null : header);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0, 0), await CountsAsync(api));
        Assert.IsType<Held>(await StateAsync(api, reservation));
        var logged = Assert.Single(factory.Services.GetFakeLogCollector().GetSnapshot(), r => r.Id.Id == 1301);
        Assert.Equal(LogLevel.Warning, logged.Level);
    }

    [Fact]
    public async Task Duplicate_event_is_processed_once()
    {
        var (factory, api) = await CreateApiAsync();
        await using var _ = factory;
        using var client = Anonymous(factory);
        var reservation = await ReserveAsync(factory);
        var payload = Completed("evt_dup_1", "cs_dup_1", reservation);

        // Stripe's retries and resends deliver the same event again; two API instances may get it at the same moment.
        using var first = await PostAsync(client, payload, Sign(payload, Secret, DateTimeOffset.UtcNow));
        using var second = await PostAsync(client, payload, Sign(payload, Secret, DateTimeOffset.UtcNow));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostAsync(client, payload, Sign(payload, Secret, DateTimeOffset.UtcNow))));

        Assert.All([first, second, .. concurrent], r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal((1, 1), await CountsAsync(api)); // one processed event, one deposit
        Assert.IsType<Confirmed>(await StateAsync(api, reservation));
        foreach (var r in concurrent)
        {
            r.Dispose();
        }
    }

    [Theory]
    [InlineData(true)] // the abandoned page's expiry is delivered first, then the paid page's completion
    [InlineData(false)] // and the other way round
    public async Task Expired_then_completed_out_of_order_ends_confirmed_if_unit_free(bool expiredFirst)
    {
        var (factory, api) = await CreateApiAsync();
        await using var _ = factory;
        using var client = Anonymous(factory);
        var reservation = await ReserveAsync(factory);
        var expired = Expired("evt_ooo_expired", "cs_ooo_abandoned", reservation);
        var completed = Completed("evt_ooo_completed", "cs_ooo_paid", reservation);

        foreach (var payload in expiredFirst ? new[] { expired, completed } : [completed, expired])
        {
            using var response = await PostAsync(client, payload, Sign(payload, Secret, DateTimeOffset.UtcNow));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The unit is still this reservation's (held, nobody else's): the payment confirms it, whatever the order.
        Assert.IsType<Confirmed>(await StateAsync(api, reservation));
        await using var payments = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.Equal(1, await payments.ExpiredCheckoutSessions.CountAsync(Ct));
        Assert.Equal(5000m, (await payments.Deposits.SingleAsync(Ct)).Amount);
    }

    [Fact]
    public async Task Webhook_is_anonymous_but_signature_protected()
    {
        var (factory, api) = await CreateApiAsync();
        await using var _ = factory;
        var reservation = await ReserveAsync(factory);
        var payload = Completed("evt_anon_1", "cs_anon_1", reservation);

        // A signed-in buyer without Stripe's signature is refused: a token is not a signature.
        using var signedIn = factory.CreateClient();
        signedIn.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer"));
        using var refused = await PostAsync(signedIn, payload, null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.IsType<Held>(await StateAsync(api, reservation));

        // No token, no tenant header, a valid signature: accepted.
        using var anonymous = Anonymous(factory);
        using var accepted = await PostAsync(anonymous, payload, Sign(payload, Secret, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.IsType<Confirmed>(await StateAsync(api, reservation));
    }

    // The raw body matters: the same event re-serialised (as model binding would) is different bytes.
    [Fact]
    public async Task Reserialised_body_fails_verification()
    {
        var (factory, api) = await CreateApiAsync();
        await using var _ = factory;
        using var client = Anonymous(factory);
        var reservation = await ReserveAsync(factory);
        var payload = Completed("evt_raw_1", "cs_raw_1", reservation);
        var header = Sign(payload, Secret, DateTimeOffset.UtcNow);

        using var response = await PostAsync(client, JsonSerializer.Serialize(JsonElement.Parse(payload)), header);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0, 0), await CountsAsync(api));
    }

    private async Task<(WebApplicationFactory<Program> Factory, ApiFactory Api)> CreateApiAsync()
    {
        var api = await database.CreateApiAsync();
        return (api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Payments:WebhookSecret", Secret);
            builder.ConfigureServices(services => services.AddLogging(logging => logging.AddFakeLogging()));
        }), api);
    }

    private static HttpClient Anonymous(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        return client;
    }

    private static async Task<ReservationId> ReserveAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{EndpointTests.RiversidePlot1}/reservations")
        {
            Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer"));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new ReservationId(JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)).GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string payload, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (signature is not null)
        {
            request.Headers.Add("Stripe-Signature", signature);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<(int Events, int Deposits)> CountsAsync(ApiFactory api)
    {
        await using var payments = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        return (await payments.ProcessedWebhookEvents.CountAsync(Ct), await payments.Deposits.CountAsync(Ct));
    }

    private static async Task<ReservationState> StateAsync(ApiFactory api, ReservationId id)
    {
        await using var sales = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        return (await sales.Reservations.AsNoTracking().SingleAsync(r => r.Id == id, Ct)).State;
    }

    /// <summary>Stripe's scheme: t=timestamp, v1=hex(HMAC-SHA256(secret, "{t}.{payload}")).</summary>
    private static string Sign(string payload, string secret, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }

    private static string Completed(string eventId, string sessionId, ReservationId reservation) =>
        Event(eventId, "checkout.session.completed", sessionId, reservation, "complete", "paid");

    private static string Expired(string eventId, string sessionId, ReservationId reservation) =>
        Event(eventId, "checkout.session.expired", sessionId, reservation, "expired", "unpaid");

    /// <summary>An event shaped like Stripe's, indented as Stripe sends it.</summary>
    private static string Event(string eventId, string type, string sessionId, ReservationId reservation, string status, string paymentStatus) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["api_version"] = Stripe.StripeConfiguration.ApiVersion,
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["livemode"] = false,
            ["type"] = type,
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = new Dictionary<string, object?>
                {
                    ["id"] = sessionId,
                    ["object"] = "checkout.session",
                    ["client_reference_id"] = reservation.Value.ToString(),
                    ["metadata"] = new Dictionary<string, string>
                    {
                        ["tenant_id"] = TestTenants.Demo.ToString(),
                        ["unit_id"] = EndpointTests.RiversidePlot1,
                        ["reservation_id"] = reservation.Value.ToString(),
                    },
                    ["amount_total"] = 500000,
                    ["currency"] = "gbp",
                    ["payment_intent"] = status == "complete" ? "pi_" + sessionId : null,
                    ["status"] = status,
                    ["payment_status"] = paymentStatus,
                    ["livemode"] = false,
                },
            },
        }, Indented);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
