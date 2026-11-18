using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// C8 Lab 1's acceptance tests, through the API and the real Stripe adapter. Stripe.net talks to a stub HTTP handler
/// that records what would have been sent to Stripe: no key, no network, safe in CI.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class DepositCheckoutTests(PostgresFixture database)
{
    private const string WebBaseUrl = "http://web.test/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, HttpStatusCode> Callers => new()
    {
        { "owner", "held", HttpStatusCode.OK },
        { "other-buyer", "held", HttpStatusCode.NotFound }, // not theirs: as if it did not exist
        { "sales-agent", "held", HttpStatusCode.Forbidden },
        { "finance", "held", HttpStatusCode.Forbidden },
        { "admin", "held", HttpStatusCode.Forbidden },
        { "owner", "confirmed", HttpStatusCode.Conflict },
        { "owner", "cancelled", HttpStatusCode.Conflict },
    };

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task Session_created_only_for_held_reservation_owned_by_caller(string caller, string state, HttpStatusCode expected)
    {
        var stripe = new StubStripe();
        var api = await database.CreateApiAsync();
        await using var factory = WithStubStripe(api, stripe);
        using var client = factory.CreateClient();
        var reservation = await ReserveAsync(client, 5000m);
        if (state != "held")
        {
            await using var db = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
            var row = await db.Reservations.SingleAsync(r => r.Id == new ReservationId(reservation.GetProperty("id").GetGuid()), Ct);
            Assert.IsType<Transitioned>((state == "confirmed" ? row.Confirm(DateTimeOffset.UtcNow) : row.Cancel(DateTimeOffset.UtcNow)).Value);
            await db.SaveChangesAsync(Ct);
        }

        using var response = await StartCheckoutAsync(client, reservation, caller);

        Assert.True(expected == response.StatusCode, $"{caller} on a {state} reservation: expected {expected}, got {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, stripe.Sessions.Count); // Stripe is called only when the session is allowed
    }

    [Fact]
    public async Task Session_amount_matches_deposit_in_minor_units()
    {
        var stripe = new StubStripe();
        await using var factory = WithStubStripe(await database.CreateApiAsync(), stripe);
        using var client = factory.CreateClient();
        var reservation = await ReserveAsync(client, 5432.10m);
        var id = reservation.GetProperty("id").GetGuid().ToString();

        using var response = await StartCheckoutAsync(client, reservation, "owner");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(stripe.Sessions);
        Assert.Equal(("payment", "1", "543210", "gbp"), (sent["mode"], sent["line_items[0][quantity]"], sent["line_items[0][price_data][unit_amount]"], sent["line_items[0][price_data][currency]"]));
        Assert.Equal((id, TestTenants.Demo.ToString(), EndpointTests.RiversidePlot1, id),
            (sent["client_reference_id"], sent["metadata[tenant_id]"], sent["metadata[unit_id]"], sent["metadata[reservation_id]"]));
        Assert.Equal(($"{WebBaseUrl}my-home?deposit=paid&reservation={id}", $"{WebBaseUrl}my-home?deposit=cancelled&reservation={id}"),
            (sent["success_url"], sent["cancel_url"]));
        var body = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(StubStripe.SessionId, body.GetProperty("sessionId").GetString());
    }

    // The brief: "expires_at = the reservation's expiry". Stripe allows 30 minutes to 24 hours, so a hold that ends soon
    // closes the page exactly when the hold ends (to the second Stripe stores), and a longer one (the 7-day default) is
    // capped 23 hours after the start of the hour (C8 Lab 3). Either way the page never outlives the reservation.
    [Theory]
    [InlineData("01:00:00", false)]
    [InlineData("7.00:00:00", true)]
    public async Task Session_expires_with_reservation(string holdPeriod, bool capped)
    {
        var stripe = new StubStripe();
        await using var factory = WithStubStripe(await database.CreateApiAsync(), stripe, ("Reservation:HoldPeriod", holdPeriod));
        using var client = factory.CreateClient();
        var reservation = await ReserveAsync(client, 5000m);
        var before = DateTimeOffset.UtcNow;

        using var response = await StartCheckoutAsync(client, reservation, "owner");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(Assert.Single(stripe.Sessions)["expires_at"], System.Globalization.CultureInfo.InvariantCulture));
        var reservationExpiresAt = reservation.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.True(expiresAt <= reservationExpiresAt, $"The page closes at {expiresAt:O}, after the reservation's hold ends at {reservationExpiresAt:O}.");
        if (capped)
        {
            Assert.InRange(expiresAt, before.AddHours(22), DateTimeOffset.UtcNow.AddHours(23)); // 23 h after the start of the hour
        }
        else
        {
            Assert.Equal(reservationExpiresAt.ToUnixTimeSeconds(), expiresAt.ToUnixTimeSeconds());
        }
    }

    private static WebApplicationFactory<Program> WithStubStripe(ApiFactory api, StubStripe stripe, params (string Key, string Value)[] settings) =>
        api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Payments:WebBaseUrl", WebBaseUrl);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services => services.AddSingleton<IStripeClient>(
                new StripeClient("stub-key-not-a-secret", httpClient: new SystemNetHttpClient(new HttpClient(stripe), maxNetworkRetries: 0))));
        });

    private static async Task<JsonElement> ReserveAsync(HttpClient client, decimal deposit)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{EndpointTests.RiversidePlot1}/reservations")
        {
            Content = JsonContent.Create(new { deposit, currency = "GBP" }),
        };
        request.Headers.Authorization = Bearer("owner");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> StartCheckoutAsync(HttpClient client, JsonElement reservation, string caller)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/reservations/{reservation.GetProperty("id").GetGuid()}/deposit-session");
        request.Headers.Authorization = Bearer(caller);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request, Ct);
    }

    private static AuthenticationHeaderValue Bearer(string caller) => new("Bearer", caller switch
    {
        "owner" => TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer"),
        "other-buyer" => TestTokens.Access(TestTokens.Users.OtherBuyer.ToString(), "buyer"),
        _ => TestTokens.Access(TestTokens.Users.Staff(caller).ToString(), caller),
    });

    /// <summary>Stands in for api.stripe.com: records each Checkout Session create and answers like Stripe would.</summary>
    private sealed class StubStripe : HttpMessageHandler
    {
        public const string SessionId = "cs_test_stub_session";

        public List<Dictionary<string, string>> Sessions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/checkout/sessions", request.RequestUri!.AbsolutePath);
            var form = QueryHelpers.ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken)).ToDictionary(f => f.Key, f => f.Value.ToString());
            Sessions.Add(form);
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = SessionId,
                ["object"] = "checkout.session",
                ["url"] = "https://checkout.stripe.com/c/pay/" + SessionId,
                ["expires_at"] = long.Parse(form["expires_at"], System.Globalization.CultureInfo.InvariantCulture),
                ["client_reference_id"] = form["client_reference_id"],
                ["livemode"] = false,
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
