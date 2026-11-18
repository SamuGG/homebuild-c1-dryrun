using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeBuild.Api.Modules.Sales;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stripe;

namespace HomeBuild.Api.Tests;

/// <summary>C8 Lab 3's acceptance tests: Idempotency-Key on the reservation and deposit-session POSTs, and on to Stripe.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class IdempotencyTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ReservePath => $"/v1/units/{EndpointTests.RiversidePlot1}/reservations";

    [Fact]
    public async Task Same_key_same_body_replays_response()
    {
        await using var api = await database.CreateApiAsync();
        using var client = api.CreateClient();
        var key = Guid.NewGuid();

        using var first = await SendAsync(client, ReservePath, "owner", key, new { deposit = 5000m, currency = "GBP" });
        using var retry = await SendAsync(client, ReservePath, "owner", key, new { deposit = 5000m, currency = "GBP" });

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, retry.StatusCode));
        Assert.Equal(first.Headers.Location, retry.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await retry.Content.ReadAsStringAsync(Ct));
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Idempotency-Replayed")));
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        await using var sales = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.Equal(1, await sales.Reservations.CountAsync(Ct)); // the retry did not run the reservation again
    }

    [Fact]
    public async Task Same_key_different_body_returns_422()
    {
        await using var api = await database.CreateApiAsync();
        using var client = api.CreateClient();
        var key = Guid.NewGuid();

        using var first = await SendAsync(client, ReservePath, "owner", key, new { deposit = 5000m, currency = "GBP" });
        using var changed = await SendAsync(client, ReservePath, "owner", key, new { deposit = 6000m, currency = "GBP" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Equal("Idempotency-Key reused", JsonElement.Parse(await changed.Content.ReadAsStringAsync(Ct)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Concurrent_same_key_returns_409_for_second()
    {
        var stripe = new KeyedStubStripe { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var api = await database.CreateApiAsync();
        await using var factory = WithStripe(api, stripe);
        using var client = factory.CreateClient();
        var reservation = await ReserveAsync(client);
        var key = Guid.NewGuid();
        var path = $"/v1/reservations/{reservation}/deposit-session";

        var first = SendAsync(client, path, "owner", key); // held inside the Stripe call
        await stripe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var second = await SendAsync(client, path, "owner", key);
        stripe.Hold.SetResult();
        using var firstResponse = await first;
        using var afterwards = await SendAsync(client, path, "owner", key);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("1", Assert.Single(second.Headers.GetValues("Retry-After")));
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (firstResponse.StatusCode, afterwards.StatusCode)); // then it replays
        Assert.Single(stripe.Requests);
    }

    // The productive failure's test (C8 Lab 3): the same key on two API instances. Only a shared, unique key in the
    // database makes the second wait; anything held in one process's memory lets both run.
    [Fact]
    public async Task Concurrent_same_key_on_two_instances_returns_409_for_second()
    {
        var stripe = new KeyedStubStripe { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var api = await database.CreateApiAsync();
        await using var instanceA = WithStripe(api, stripe);
        await using var instanceB = WithStripe(api, stripe);
        using var clientA = instanceA.CreateClient();
        using var clientB = instanceB.CreateClient();
        var reservation = await ReserveAsync(clientA);
        var key = Guid.NewGuid();
        var path = $"/v1/reservations/{reservation}/deposit-session";

        var first = SendAsync(clientA, path, "owner", key);
        await stripe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        using var second = await SendAsync(clientB, path, "owner", key);
        stripe.Hold.SetResult();
        using var firstResponse = await first;

        TestContext.Current.TestOutputHelper?.WriteLine($"instance A: {(int)firstResponse.StatusCode}; instance B: {(int)second.StatusCode}; Stripe calls: {stripe.Requests.Count}");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Single(stripe.Requests);
    }

    [Theory]
    [InlineData("reserve")]
    [InlineData("deposit-session")]
    public async Task Missing_key_returns_400(string endpoint)
    {
        await using var api = await database.CreateApiAsync();
        await using var factory = WithStripe(api, new KeyedStubStripe());
        using var client = factory.CreateClient();
        var path = endpoint == "reserve" ? ReservePath : $"/v1/reservations/{await ReserveAsync(client)}/deposit-session";

        using var response = await SendAsync(client, path, "owner", key: null, body: endpoint == "reserve" ? new { deposit = 5000m, currency = "GBP" } : null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Idempotency-Key required", JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)).GetProperty("title").GetString());
    }

    // Scoped to the caller: another buyer sending the same key gets their own outcome, never the first buyer's response.
    [Fact]
    public async Task Same_key_from_another_caller_is_a_different_request()
    {
        await using var api = await database.CreateApiAsync();
        using var client = api.CreateClient();
        var key = Guid.NewGuid();

        using var owner = await SendAsync(client, ReservePath, "owner", key, new { deposit = 5000m, currency = "GBP" });
        using var other = await SendAsync(client, ReservePath, "other-buyer", key, new { deposit = 5000m, currency = "GBP" });

        Assert.Equal(HttpStatusCode.Created, owner.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode); // the unit is taken: their own answer, not the owner's 201
        Assert.False(other.Headers.Contains("Idempotency-Replayed"));
        Assert.DoesNotContain(owner.Headers.Location!.ToString().Split('/')[^1], await other.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    // Two clicks from two renders (two HomeBuild keys) for one reservation: HomeBuild sends Stripe the same derived key,
    // so Stripe returns one session. The same HomeBuild key twice does not even reach Stripe the second time.
    [Fact]
    public async Task Retried_stripe_call_creates_one_session()
    {
        var stripe = new KeyedStubStripe();
        await using var api = await database.CreateApiAsync();
        await using var factory = WithStripe(api, stripe);
        using var client = factory.CreateClient();
        var reservation = await ReserveAsync(client);
        var path = $"/v1/reservations/{reservation}/deposit-session";
        var repeatedKey = Guid.NewGuid();

        using var a = await SendAsync(client, path, "owner", repeatedKey);
        using var b = await SendAsync(client, path, "owner", repeatedKey);
        using var c = await SendAsync(client, path, "owner", Guid.NewGuid());

        var sessions = await Task.WhenAll(new[] { a, b, c }.Select(async r => JsonElement.Parse(await r.Content.ReadAsStringAsync(Ct)).GetProperty("sessionId").GetString()));
        Assert.Single(sessions.Distinct());
        Assert.Equal(2, stripe.Requests.Count); // b was replayed by HomeBuild
        Assert.Single(stripe.Requests.Distinct());
        Assert.StartsWith($"deposit-session:{reservation}:", stripe.Requests[0], StringComparison.Ordinal);
        Assert.Single(stripe.Sessions);
    }

    private static WebApplicationFactory<Program> WithStripe(ApiFactory api, KeyedStubStripe stripe) =>
        api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Payments:WebBaseUrl", "http://web.test/");
            builder.ConfigureTestServices(services => services.AddSingleton<IStripeClient>(
                new StripeClient("stub-key-not-a-secret", httpClient: new SystemNetHttpClient(new HttpClient(stripe), maxNetworkRetries: 0))));
        });

    private static async Task<Guid> ReserveAsync(HttpClient client)
    {
        using var response = await SendAsync(client, ReservePath, "owner", Guid.NewGuid(), new { deposit = 5000m, currency = "GBP" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string caller, Guid? key, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller switch
        {
            "owner" => TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer"),
            _ => TestTokens.Access(TestTokens.Users.OtherBuyer.ToString(), "buyer"),
        });
        if (key is { } k)
        {
            request.Headers.Add("Idempotency-Key", k.ToString());
        }
        else
        {
            request.Headers.TryAddWithoutValidation("X-No-Idempotency-Key", "1"); // the factory's default adds one otherwise
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Behaves like Stripe's idempotency: the same Idempotency-Key returns the same session.</summary>
    private sealed class KeyedStubStripe : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public Dictionary<string, string> Sessions { get; } = [];

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Hold { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.GetValues("Idempotency-Key").Single();
            var form = QueryHelpers.ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken));
            string id;
            lock (Requests)
            {
                Requests.Add(key);
                if (!Sessions.TryGetValue(key, out id!))
                {
                    id = $"cs_test_stub_{Sessions.Count + 1}";
                    Sessions[key] = id;
                }
            }

            Entered.TrySetResult();
            if (Hold is not null)
            {
                await Hold.Task.WaitAsync(cancellationToken);
            }

            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id, ["object"] = "checkout.session", ["url"] = "https://checkout.stripe.com/c/pay/" + id,
                ["expires_at"] = long.Parse(form["expires_at"]!, System.Globalization.CultureInfo.InvariantCulture), ["livemode"] = false,
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
